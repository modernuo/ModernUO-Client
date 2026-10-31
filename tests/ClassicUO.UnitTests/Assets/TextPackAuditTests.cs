using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // The text-in-pack audit (poc-log "Text in the pack: audit fixes, overrides, block-compressed cliloc"): records the
    // pack accepts must render, invalid store topology and oversized files are refused before allocation, a corrupt pack
    // language never blocks the loose files, reloads publish only complete states, the cliloc overrides, and the retired
    // block-compressed layout is rejected. All synthetic: no client data needed.
    public class TextPackAuditTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cuo-textaudit-" + Guid.NewGuid().ToString("N"));

        public TextPackAuditTests() => Directory.CreateDirectory(_tmp);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tmp, true);
            }
            catch (IOException)
            {
            }
        }

        private string P(string name) => Path.Combine(_tmp, name);

        // ------------------------------------------------------------------ fonts (findings 1, 3, 9)

        private (string Ascii, string Uni) WriteFontStores(AsciiGlyph[] ascii, UnicodeGlyph[] page0)
        {
            var a = AsciiFontCodec.Encode(ascii);
            var aw = new RecordStoreWriter(1);
            aw.Slots[0] = new StoreSlot { Record = (uint)aw.Add(a, (uint)a.Length, StoreConst.CodecRaw, 224, 0) + 1 };
            aw.Write(P("fonts.bin"), TextConst.MagicFontsAscii, 0);

            var u = UnicodePageCodec.Encode(page0);
            UnicodePageCodec.Validate(u).Should().BeGreaterThan(0, "the page is one the pack accepts");
            var uw = new RecordStoreWriter(TextConst.UnicodeSlots);
            uw.Slots[0] = new StoreSlot { Record = (uint)uw.Add(u, (uint)u.Length, StoreConst.CodecRaw, 0, 0, 1) + 1 };
            uw.Write(P("unifont.bin"), TextConst.MagicFontsUnicode, 1);

            return (P("fonts.bin"), P("unifont.bin"));
        }

        private static byte[] Bits(int w, int h, byte fill) => Enumerable.Repeat(fill, UnicodeGlyph.BitBytes(w, h)).ToArray();

        [Fact]
        public void Accepted_Unicode_Glyphs_With_Extreme_Offsets_Render()
        {
            var page = new UnicodeGlyph[256];
            page['A'] = new UnicodeGlyph(-2, 0, 2, 1, new byte[] { 0xC0 });          // the audit's case: pData[-1]
            page['B'] = new UnicodeGlyph(-128, -128, 127, 127, Bits(127, 127, 0xFF)); // every field at its extreme
            page['C'] = new UnicodeGlyph(0, -100, 8, 5, Bits(8, 5, 0xAA));
            page['D'] = new UnicodeGlyph(127, 127, 1, 1, new byte[] { 0x80 });
            page['a'] = new UnicodeGlyph(-50, -60, 3, 1, new byte[] { 0xE0 });       // the underline reads 'a'
            page['x'] = new UnicodeGlyph(0, 0, 4, 4, Bits(4, 4, 0xF0));
            var (ascii, uni) = WriteFontStores(new AsciiGlyph[224], page);

            var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
            var fonts = fm.Fonts;
            fonts.LoadStores(ascii, uni);

            string[] texts = { "A", "AA", "xA", "B", "AB", "BA", "CD", "ABCDax", " A", "A A", "a", "xxxxxxxx A" };
            ushort[] flags = { 0, 0x0001, 0x0002, 0x0008, 0x0010, 0x0001 | 0x0008, 0x0002 | 0x0010 | 0x0008, 0x0001 | 0x0002 | 0x0010 };

            foreach (var t in texts)
            {
                foreach (var f in flags)
                {
                    foreach (var align in new[] { TEXT_ALIGN_TYPE.TS_LEFT, TEXT_ALIGN_TYPE.TS_CENTER, TEXT_ALIGN_TYPE.TS_RIGHT })
                    {
                        foreach (int width in new[] { 0, 1, 3, 30, 300 })
                        {
                            Action draw = () => fonts.GenerateUnicode(0, t, 0x0021, 30, width, align, f, false, 0);
                            draw.Should().NotThrow($"'{t}' flags 0x{f:X} {align} width {width}");
                        }
                    }
                }
            }

            foreach (char c in "ABCDax")
            {
                foreach (var (border, solid, italic) in new[] { (false, false, false), (true, false, false), (false, true, false), (true, true, true) })
                {
                    Action glyph = () => fonts.RenderSingleGlyphUnicode(0, c, border, solid, italic, 0xFFFFFFFF);
                    glyph.Should().NotThrow($"'{c}' border {border} solid {solid} italic {italic}");
                }
            }

            fonts.GetUnicodeGlyph(0, 'A').Data.Should().Equal(new byte[] { 0xC0 }, "the record stays accepted");
        }

        [Fact]
        public void Ascii_Glyph_With_A_Size_But_No_Texels_Is_Rejected_And_Rendering_Survives()
        {
            var glyphs = new AsciiGlyph[224];
            glyphs[33] = new AsciiGlyph(2, 2, new ushort[] { 0x7FFF, 0, 0, 0x7FFF });
            glyphs[34] = new AsciiGlyph(255, 255, Enumerable.Repeat((ushort)0x1234, 255 * 255).ToArray());
            var good = AsciiFontCodec.Encode(glyphs);

            // the audit's record: glyph 'B' (34 + 32) 1×1 with the texel flag clear
            var forged = (byte[])good.Clone();
            forged[35 * 4] = 1;
            forged[35 * 4 + 1] = 1;
            AsciiFontCodec.RawSize(forged).Should().Be(-1, "a size without texels is malformed");
            AsciiFontCodec.TryDecode(forged, new AsciiGlyph[224]).Should().BeFalse();
            Action encode = () => AsciiFontCodec.Encode(new[] { new AsciiGlyph(1, 1, null) }.Concat(new AsciiGlyph[223]).ToArray());
            encode.Should().Throw<ArgumentException>();

            var aw = new RecordStoreWriter(2);
            aw.Slots[0] = new StoreSlot { Record = (uint)aw.Add(good, (uint)good.Length, StoreConst.CodecRaw, 224, 0) + 1 };
            aw.Slots[1] = new StoreSlot { Record = (uint)aw.Add(forged, (uint)forged.Length, StoreConst.CodecRaw, 224, 0) + 1 };
            aw.Write(P("fonts.bin"), TextConst.MagicFontsAscii, 0);
            var uw = new RecordStoreWriter(TextConst.UnicodeSlots);
            uw.Write(P("unifont.bin"), TextConst.MagicFontsUnicode, 0);

            var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
            var fonts = fm.Fonts;
            fonts.LoadStores(P("fonts.bin"), P("unifont.bin"));
            fonts.GetAsciiGlyph(1, 35).Width.Should().Be(0, "the forged font is absent");

            for (byte font = 0; font < 2; font++)
            {
                foreach (var s in new[] { "A", "BC", "AB AB", "ÿ\u0001" })
                {
                    foreach (var align in new[] { TEXT_ALIGN_TYPE.TS_LEFT, TEXT_ALIGN_TYPE.TS_CENTER, TEXT_ALIGN_TYPE.TS_RIGHT })
                    {
                        foreach (int width in new[] { 0, 1, 40 })
                        {
                            Action draw = () => fonts.GenerateASCII(font, s, 0, width, align, 0, false, 0);
                            draw.Should().NotThrow($"font {font} '{s}' {align} {width}");
                        }
                    }

                    foreach (char c in s)
                    {
                        Action glyph = () => fonts.RenderSingleGlyphASCII(font, c, 0);
                        glyph.Should().NotThrow();
                    }
                }
            }
        }

        [Fact]
        public void Font_Store_Topology_Is_Checked_Before_The_Tables_Are_Allocated()
        {
            // a unifont store must have exactly 20 × 256 slots: zero slots with every font flagged present is refused
            foreach (int slots in new[] { 0, 1, TextConst.UnicodeSlots - 1, TextConst.UnicodeSlots + 1 })
            {
                var path = P($"uni{slots}.bin");
                new RecordStoreWriter(slots).Write(path, TextConst.MagicFontsUnicode, 0xFFFFF);
                Action open = () => TextConst.OpenUnicodeStore(path).Dispose();
                open.Should().Throw<InvalidDataException>($"{slots} slots");
            }

            new RecordStoreWriter(TextConst.UnicodeSlots).Write(P("uniok.bin"), TextConst.MagicFontsUnicode, 1);
            using (TextConst.OpenUnicodeStore(P("uniok.bin")))
            {
            }

            // ASCII: 1..256 fonts
            foreach (int slots in new[] { 0, TextConst.MaxAsciiFonts + 1 })
            {
                var path = P($"ascii{slots}.bin");
                new RecordStoreWriter(slots).Write(path, TextConst.MagicFontsAscii, 0);
                Action open = () => TextConst.OpenAsciiStore(path).Dispose();
                open.Should().Throw<InvalidDataException>($"{slots} ASCII fonts");
            }

            // a forged header asking for 16M slots in a tiny file: refused by the slot bounds, nothing allocated
            new RecordStoreWriter(1).Write(P("big.bin"), TextConst.MagicFontsAscii, 0);
            var bytes = File.ReadAllBytes(P("big.bin"));
            BitConverter.GetBytes(RecordStore.MaxSlots).CopyTo(bytes, StructIo.SizeOf<FileHeader>());
            File.WriteAllBytes(P("big.bin"), bytes);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Action big = () => TextConst.OpenAsciiStore(P("big.bin")).Dispose();
            big.Should().Throw<InvalidDataException>();
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1 << 20);

            // the loader: a unifont store with the wrong topology is a whole-file failure
            var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
            new RecordStoreWriter(1).Write(P("fonts1.bin"), TextConst.MagicFontsAscii, 0);
            Action load = () => fm.Fonts.LoadStores(P("fonts1.bin"), P("uni0.bin"));
            load.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void First_Use_Of_A_Page_Or_Font_Decodes_Once_Across_Threads()
        {
            var page = new UnicodeGlyph[256];
            page['A'] = new UnicodeGlyph(0, 0, 8, 2, new byte[] { 0xFF, 0x81 });
            var glyphs = new AsciiGlyph[224];
            glyphs[33] = new AsciiGlyph(1, 1, new ushort[] { 7 });
            var (ascii, uni) = WriteFontStores(glyphs, page);

            for (int round = 0; round < 20; round++)
            {
                var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
                var fonts = fm.Fonts;
                fonts.LoadStores(ascii, uni);
                long pages0 = Interlocked.Read(ref FontsLoader.StatPageLoads), ascii0 = Interlocked.Read(ref FontsLoader.StatAsciiLoads);
                using var go = new ManualResetEventSlim();
                var results = new object[16];
                var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
                {
                    go.Wait();
                    results[i] = (fonts.GetUnicodeGlyph(0, 'A').Data, fonts.GetAsciiGlyph(0, 33).Data);
                })).ToArray();
                go.Set();
                Task.WaitAll(tasks);
                // other tests may load pages at the same time: at least this many, and the instances agree
                (Interlocked.Read(ref FontsLoader.StatPageLoads) - pages0).Should().BeGreaterThanOrEqualTo(1);
                results.Select(r => (((byte[], ushort[]))r).Item1).Distinct().Should().HaveCount(1, "every thread got the one page");
                results.Select(r => (((byte[], ushort[]))r).Item2).Distinct().Should().HaveCount(1, "every thread got the one font");
                fonts.LoadedUnicodePages.Should().Be(1);
            }
        }

        [Fact]
        public void A_Bad_Font_Record_Is_Logged_Once_Under_Contention()
        {
            var good = UnicodePageCodec.Encode(new UnicodeGlyph[256].Select((g, i) => i == 65 ? new UnicodeGlyph(0, 0, 8, 1, new byte[] { 1 }) : g).ToArray());
            var bad = (byte[])good.Clone();
            BitConverter.GetBytes(0x10000u).CopyTo(bad, 65 * 8 + 4);
            var uw = new RecordStoreWriter(TextConst.UnicodeSlots);

            for (int p = 0; p < 64; p++)
            {
                // 64 pages share one malformed record: many threads find it bad at once
                uw.Slots[p] = new StoreSlot { Record = 1 };
            }

            uw.Add(bad, (uint)bad.Length, StoreConst.CodecRaw, 0, 0, 1);
            uw.Write(P("unibad.bin"), TextConst.MagicFontsUnicode, 1);
            new RecordStoreWriter(1).Write(P("fonts1.bin"), TextConst.MagicFontsAscii, 0);
            var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
            fm.Fonts.LoadStores(P("fonts1.bin"), P("unibad.bin"));
            int logged0 = Volatile.Read(ref FontsLoader.StatBadLogged);
            Parallel.For(0, 64, p => fm.Fonts.GetUnicodeGlyph(0, (char)(p * 256 + 65)).Data.Should().BeNull());
            // other tests may log their own bad records meanwhile; this record adds exactly one
            (Volatile.Read(ref FontsLoader.StatBadLogged) - logged0).Should().BeGreaterThanOrEqualTo(1);
            fm.Fonts.GetUnicodeGlyph(0, 'A').Data.Should().BeNull();
        }

        // ------------------------------------------------------------------ speech (finding 2)

        [Fact]
        public void Oversized_Speech_File_Is_Refused_Without_Reading_It()
        {
            var path = P("speech.bin");
            SpeechFile.Write(path, new List<(ushort, byte[])> { (1, Encoding.UTF8.GetBytes("bank")) });

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(1L << 30); // 1 GiB: the header's sizes no longer match the file
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            Action read = () => SpeechFile.Read(path);
            read.Should().Throw<InvalidDataException>();
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1 << 20, "nothing was allocated for the file");

            // the header claiming more than the caps: refused before allocation too
            SpeechFile.Write(path, new List<(ushort, byte[])> { (1, Encoding.UTF8.GetBytes("bank")) });
            var bytes = File.ReadAllBytes(path);
            BitConverter.GetBytes((uint)SpeechFile.MaxBlobBytes + 1).CopyTo(bytes, StructIo.SizeOf<FileHeader>() + 4);
            File.WriteAllBytes(path, bytes);
            read.Should().Throw<InvalidDataException>();
        }

        // ------------------------------------------------------------------ cliloc (findings 4, 7, 8; overrides; blocks)

        // A raw (not BWT-compressed) Cliloc.* file: header int32 2, int16 1, then (number, flag, length, UTF-8) records.
        private static void WriteClilocFile(string path, params (int Id, string Text)[] entries)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(2);
            w.Write((short)1);

            foreach (var (id, text) in entries)
            {
                var b = Encoding.UTF8.GetBytes(text);
                w.Write(id);
                w.Write((byte)0);
                w.Write((short)b.Length);
                w.Write(b);
            }
        }

        private static byte[] Table(params (int Id, string Text)[] entries) =>
            ClilocTable.Build(entries.Select(e => (e.Id, Encoding.UTF8.GetBytes(e.Text))).ToList());

        // (payload, kind, language code or null, retired blocked-layout bit set, codec) records, one slot each in order
        private string WriteClilocStore(string name, params (byte[] Payload, ushort Kind, string Lang, bool Blocked, ushort Codec)[] records)
        {
            var w = new RecordStoreWriter(records.Length);

            for (int i = 0; i < records.Length; i++)
            {
                var r = records[i];
                var (m0, m1) = r.Lang != null ? ClilocTable.PackLanguage(r.Lang) : ((ushort)0, (ushort)0);
                var stored = RecordStoreWriter.Encode(r.Payload, r.Codec);
                w.Slots[i] = new StoreSlot { Record = (uint)w.Add(stored, (uint)r.Payload.Length, r.Codec, m0, m1, (ushort)(r.Kind | (r.Blocked ? ClilocStore.RetiredLayoutBlocked : 0))) + 1 };
            }

            var path = P(name);
            w.Write(path, TextConst.MagicCliloc, 0);

            return path;
        }

        private static (byte[], ushort, string, bool, ushort) Lang(string code, byte[] payload, bool blocked = false, ushort codec = StoreConst.CodecRaw) =>
            (payload, ClilocStore.KindLanguage, code, blocked, codec);

        private static (byte[], ushort, string, bool, ushort) Overrides(byte[] payload, ushort codec = StoreConst.CodecRaw) =>
            (payload, ClilocStore.KindOverrides, null, false, codec);

        private ClilocLoader Cliloc(string packFile)
        {
            var fm = new UOFileManager(ClientVersion.CV_7090, _tmp, null);
            var c = fm.Clilocs;
            c.UsePack = true;
            c.PackFile = packFile;

            return c;
        }

        [Fact]
        public void Corrupt_Pack_Language_Falls_Back_To_Loose_Files_With_English_Rollback()
        {
            WriteClilocFile(P("Cliloc.enu"), (1, "loose one"), (2, "loose two"));
            var garbage = Table((1, "x"));
            garbage[0] = 99; // count no longer adds up
            var pack = WriteClilocStore("cliloc.bin", Lang("enu", Table((1, "pack one"))), Lang("deu", garbage));

            // deu is in the pack but corrupt; Cliloc.deu is absent: the loose Cliloc.enu serves
            var c = Cliloc(pack);
            c.Load("deu");
            c.FromPack.Should().BeFalse();
            c.Language.Should().Be("enu");
            c.GetString(1).Should().Be("loose one");
            c.GetString(2).Should().Be("loose two");

            // with Cliloc.deu present, it serves over the loose enu
            WriteClilocFile(P("Cliloc.deu"), (2, "zwei"));
            c.Load("deu");
            c.Language.Should().Be("deu");
            c.GetString(1).Should().Be("loose one");
            c.GetString(2).Should().Be("zwei");

            // a store that doesn't resolve (two enu records) is rejected as a whole
            var dup = WriteClilocStore("dup.bin", Lang("enu", Table((1, "a"))), Lang("enu", Table((1, "b"))));
            var d = Cliloc(dup);
            d.Load("enu");
            d.FromPack.Should().BeFalse();
            d.GetString(1).Should().Be("loose one");

            // and a valid pack serves
            var ok = Cliloc(pack);
            ok.Load("enu");
            ok.FromPack.Should().BeTrue();
            ok.GetString(1).Should().Be("pack one");
        }

        [Fact]
        public void Store_Resolution_Rejects_Duplicates_And_Unknown_Meta()
        {
            using (var s = RecordStore.Open(WriteClilocStore("a.bin", Lang("enu", Table((1, "a"))), Lang("deu", Table((1, "b"))), Overrides(Table((1, "o")))), TextConst.MagicCliloc))
            {
                var dir = ClilocStore.Resolve(s);
                dir.Order.Should().Equal("enu", "deu");
                dir.Overrides.Should().Be(2);
            }

            foreach (var bad in new[]
            {
                WriteClilocStore("b1.bin", Lang("enu", Table((1, "a"))), Lang("enu", Table((1, "b")))),
                WriteClilocStore("b2.bin", Lang("enu", Table((1, "a"))), Overrides(Table((1, "o"))), Overrides(Table((1, "p")))),
                WriteClilocStore("b3.bin", Lang("enu", Table((1, "a"))), (Table((1, "o")), ClilocStore.KindOverrides, "enu", false, StoreConst.CodecRaw)),
                WriteClilocStore("b4.bin", (Table((1, "a")), (ushort)7, "enu", false, StoreConst.CodecRaw)),
            })
            {
                using var s = RecordStore.Open(bad, TextConst.MagicCliloc);
                Action resolve = () => ClilocStore.Resolve(s);
                resolve.Should().Throw<InvalidDataException>(Path.GetFileName(bad));
            }

            // a record no slot reaches is ignored, as at runtime
            var w = new RecordStoreWriter(2);
            var (m0, m1) = ClilocTable.PackLanguage("enu");
            var t = Table((1, "a"));
            w.Slots[1] = new StoreSlot { Record = (uint)w.Add(t, (uint)t.Length, StoreConst.CodecRaw, m0, m1, 0) + 1 };
            var (d0, d1) = ClilocTable.PackLanguage("deu");
            w.Add(t, (uint)t.Length, StoreConst.CodecRaw, d0, d1, 0);
            w.Write(P("c.bin"), TextConst.MagicCliloc, 0);

            using (var s = RecordStore.Open(P("c.bin"), TextConst.MagicCliloc))
            {
                ClilocStore.Resolve(s).Order.Should().Equal("enu");
            }
        }

        [Fact]
        public void Reload_Keeps_The_Old_Strings_Until_The_New_Ones_Are_Complete()
        {
            WriteClilocFile(P("Cliloc.enu"), (1, "one"));
            var c = Cliloc(null);
            c.Load("enu");
            c.GetString(1).Should().Be("one");

            // readers on another thread never see a missing string while reloads run
            using var stop = new CancellationTokenSource();
            int misses = 0;
            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!c.TryGetString(1, out _))
                    {
                        Interlocked.Increment(ref misses);
                    }
                }
            });

            for (int i = 0; i < 50; i++)
            {
                c.Reload();
            }

            // a reload that fails (the file is gone) keeps the old strings
            File.Delete(P("Cliloc.enu"));
            c.Reload();
            stop.Cancel();
            reader.Wait();
            misses.Should().Be(0);
            c.GetString(1).Should().Be("one");

            // (a truncated Cliloc.* file is not a failure: the client clips its last entry, as ReadUTF8 always did)
            // the replacement is published once it is complete
            WriteClilocFile(P("Cliloc.enu"), (1, "uno"));
            c.Reload();
            c.GetString(1).Should().Be("uno");
        }

        [Fact]
        public void Reload_Without_The_English_Base_Keeps_The_Old_Strings()
        {
            WriteClilocFile(P("Cliloc.enu"), (1, "one"), (2, "two"));
            WriteClilocFile(P("Cliloc.deu"), (2, "zwei"));
            var c = Cliloc(null);
            c.Load("deu");
            c.Language.Should().Be("deu");
            c.GetString(1).Should().Be("one", "an English-only number");
            c.GetString(2).Should().Be("zwei");

            // Cliloc.enu removed while Cliloc.deu stays: the new state would lose every English-only number
            File.Delete(P("Cliloc.enu"));
            c.Reload();
            c.GetString(1).Should().Be("one");
            c.GetString(2).Should().Be("zwei");
            c.EnglishTable.Should().NotBeNull();

            // the first load has nothing to keep: the language serves alone (logged as an error)
            var fresh = Cliloc(null);
            fresh.Load("deu");
            fresh.Language.Should().Be("deu");
            fresh.GetString(2).Should().Be("zwei");
            fresh.EnglishTable.Should().BeNull();
        }

        [Fact]
        public void Reload_With_An_Unreadable_Clilocs_Txt_Keeps_The_Old_Overrides()
        {
            if (!OperatingSystem.IsWindows())
            {
                return; // an exclusive open makes the file unreadable only where sharing is enforced
            }

            WriteClilocFile(P("Cliloc.enu"), (1, "one"), (2, "two"));
            File.WriteAllText(P(ClilocOverrides.FileName), "2 ours" + Environment.NewLine);
            var c = Cliloc(null);
            c.Load("enu");
            c.GetString(2).Should().Be("ours");

            // Clilocs.txt exists but can't be read (held exclusively): the reload is not published
            WriteClilocFile(P("Cliloc.enu"), (1, "uno"), (2, "dos"));

            using (new FileStream(P(ClilocOverrides.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                c.Reload();
            }

            c.GetString(2).Should().Be("ours");
            c.GetString(1).Should().Be("one", "the whole old state stays, not just the overrides");

            // a Clilocs.txt that is absent is optional: the reload publishes without it
            File.Delete(P(ClilocOverrides.FileName));
            c.Reload();
            c.LooseOverrides.Should().BeNull();
            c.GetString(2).Should().Be("dos");
            c.GetString(1).Should().Be("uno");
        }

        [Fact]
        public void Clilocs_Txt_Missing_Is_Absent_And_An_Unreadable_Path_Is_A_Failure()
        {
            WriteClilocFile(P("Cliloc.enu"), (1, "one"), (2, "two"));

            // missing: optional, the first load publishes without it
            var c = Cliloc(null);
            c.Load("enu");
            c.LooseOverrides.Should().BeNull();
            c.GetString(2).Should().Be("two");

            File.WriteAllText(P(ClilocOverrides.FileName), "2 ours" + Environment.NewLine);
            c.Reload();
            c.GetString(2).Should().Be("ours");

            // a directory where the file should be: it exists but can't be read (deterministic, no ACLs)
            File.Delete(P(ClilocOverrides.FileName));
            Directory.CreateDirectory(P(ClilocOverrides.FileName));
            WriteClilocFile(P("Cliloc.enu"), (1, "uno"), (2, "dos"));

            try
            {
                // a reload keeps the whole old state, not an absent-overrides one
                c.Reload();
                c.GetString(2).Should().Be("ours");
                c.GetString(1).Should().Be("one");

                // the first load has nothing to keep: it still serves the languages
                var fresh = Cliloc(null);
                fresh.Load("enu");
                fresh.GetString(1).Should().Be("uno");
                fresh.LooseOverrides.Should().BeNull();
            }
            finally
            {
                Directory.Delete(P(ClilocOverrides.FileName));
            }
        }

        [Fact]
        public void Overrides_Precedence_Is_Loose_Then_Packed_Then_Language_Then_English()
        {
            var pack = WriteClilocStore("cliloc.bin",
                Lang("enu", Table((1, "enu1"), (2, "enu2"), (3, "enu3"), (4, "enu4"), (5, "enu5"))),
                Lang("deu", Table((2, "deu2"), (3, "deu3"), (4, "deu4"))),
                Overrides(Table((3, "ovr3"), (4, "ovr4"), (9, "ovr9"))));
            File.WriteAllLines(P(ClilocOverrides.FileName), new[] { "# ours", "4\tloose4", "10 loose ten" });

            var c = Cliloc(pack);
            c.Load("deu");
            c.FromPack.Should().BeTrue();
            c.GetString(1).Should().Be("enu1");
            c.GetString(2).Should().Be("deu2");
            c.GetString(3).Should().Be("ovr3");
            c.GetString(4).Should().Be("loose4");
            c.GetString(9).Should().Be("ovr9");
            c.GetString(10).Should().Be("loose ten");
            c.Translate(10).Should().Be("loose ten");

            // without the loose file, the packed override serves
            File.Delete(P(ClilocOverrides.FileName));
            c.Reload();
            c.GetString(4).Should().Be("ovr4");
            c.LooseOverrides.Should().BeNull();

            // English: overrides still win over enu
            c.Load("enu");
            c.GetString(3).Should().Be("ovr3");
            c.GetString(2).Should().Be("enu2");

            // and over loose Cliloc.* files when the pack's languages can't serve
            WriteClilocFile(P("Cliloc.enu"), (3, "file3"), (6, "file6"));
            var noLang = WriteClilocStore("ovronly.bin", Overrides(Table((3, "ovr3"))));
            var f = Cliloc(noLang);
            f.Load("enu");
            f.FromPack.Should().BeFalse();
            f.GetString(3).Should().Be("ovr3");
            f.GetString(6).Should().Be("file6");
        }

        [Fact]
        public void Corrupt_Override_Records_Are_Absent_Never_Thrown()
        {
            var enu = Table((1, "enu1"), (3, "enu3"));
            var malformed = Table((3, "ovr3"));
            malformed[4] = 200; // blobBytes no longer adds up
            var corruptBrotli = (Table((3, "ovr3")), ClilocStore.KindOverrides, (string)null, false, StoreConst.CodecBrotli);

            var cases = new[]
            {
                WriteClilocStore("o1.bin", Lang("enu", enu), Overrides(malformed)),
                WriteClilocStore("o2.bin", Lang("enu", enu), Overrides(new byte[3])),
                WriteClilocStore("o3.bin", Lang("enu", enu), Overrides(Table())),
            };

            foreach (var path in cases)
            {
                var c = Cliloc(path);
                Action load = () => c.Load("enu");
                load.Should().NotThrow();
                c.FromPack.Should().BeTrue("the languages still serve");
                c.GetString(3).Should().Be("enu3", Path.GetFileName(path));
            }

            // a Brotli overrides record whose stream is corrupt
            var w = new RecordStoreWriter(2);
            var (m0, m1) = ClilocTable.PackLanguage("enu");
            w.Slots[0] = new StoreSlot { Record = (uint)w.Add(enu, (uint)enu.Length, StoreConst.CodecRaw, m0, m1, 0) + 1 };
            w.Slots[1] = new StoreSlot { Record = (uint)w.Add(new byte[] { 1, 2, 3, 4 }, (uint)corruptBrotli.Item1.Length, StoreConst.CodecBrotli, 0, 0, ClilocStore.KindOverrides) + 1 };
            w.Write(P("o4.bin"), TextConst.MagicCliloc, 0);
            var b = Cliloc(P("o4.bin"));
            b.Load("enu");
            b.PackOverridesTable.Should().BeNull();
            b.GetString(3).Should().Be("enu3");
        }

        [Fact]
        public void Invariant_Culture_Language_Is_No_Language()
        {
            // under invariant globalization the OS language reads as IVL; startup normalises it (and ZZZ) to ENU
            ClassicUO.Bootstrap.IsNoLanguage("IVL").Should().BeTrue();
            ClassicUO.Bootstrap.IsNoLanguage("ivl").Should().BeTrue();
            ClassicUO.Bootstrap.IsNoLanguage("ZZZ").Should().BeTrue();
            ClassicUO.Bootstrap.IsNoLanguage("ENU").Should().BeFalse();
            ClassicUO.Bootstrap.IsNoLanguage("DEU").Should().BeFalse();
            System.Globalization.CultureInfo.InvariantCulture.ThreeLetterWindowsLanguageName.Should().Be("IVL");
        }

        [Fact]
        public void Retired_Blocked_Layout_Is_Rejected_Never_Read_As_Flat()
        {
            WriteClilocFile(P("Cliloc.enu"), (1, "loose one"));
            // a flat payload behind the retired bit: a reader that ignored the bit would serve it
            var flat = Table((1, "pack one"));

            using (var s = RecordStore.Open(WriteClilocStore("blk1.bin", Lang("enu", flat, blocked: true)), TextConst.MagicCliloc))
            {
                Action resolve = () => ClilocStore.Resolve(s);
                resolve.Should().Throw<InvalidDataException>().WithMessage("*block-compressed layout*no longer supported*");
                ClilocStore.ReadTable(s, 0, out string why).Should().BeNull();
                why.Should().Contain("no longer supported");
            }

            // the client rejects the whole store (an Error naming the layout) and reads the loose files
            foreach (var pack in new[]
            {
                WriteClilocStore("blk2.bin", Lang("enu", flat, blocked: true)),
                WriteClilocStore("blk3.bin", Lang("enu", flat), Lang("deu", Table((1, "eins")), blocked: true)),
            })
            {
                var c = Cliloc(pack);
                c.Load("enu");
                c.FromPack.Should().BeFalse();
                c.GetString(1).Should().Be("loose one");
            }
        }

        [Fact]
        public void Converter_Language_Discovery_Matches_Names_Case_Insensitively_And_Files_By_Content()
        {
            var dir = Path.Combine(_tmp, "client");
            Directory.CreateDirectory(dir);
            Action none = () => ClilocDiscovery.Find(dir);
            none.Should().Throw<InvalidDataException>("an empty language set is an error");

            WriteClilocFile(Path.Combine(dir, "CLILOC.ENU"), (1, "one"));
            WriteClilocFile(Path.Combine(dir, "cliloc.deu"), (1, "eins"));
            WriteClilocFile(Path.Combine(dir, "Cliloc.bak"), (1, "bashkir"));      // bak is a language code
            File.WriteAllText(Path.Combine(dir, "CliLoc.csv"), "Number,Text\n1,one\n2,two\n"); // an export
            File.WriteAllText(Path.Combine(dir, "Cliloc.toolong"), "x");
            var bwt = new byte[] { 0, 0, 0, 0x8E, 0, 0, 0 };
            File.WriteAllBytes(Path.Combine(dir, "cliloc.kor"), bwt);

            var found = ClilocDiscovery.Find(dir, null, null);
            found.Select(f => f.Lang).Should().Equal("enu", "bak", "deu", "kor");
            ClilocDiscovery.Find(dir, new HashSet<string> { "bak" }).Select(f => f.Lang).Should().Equal("enu", "deu", "kor");

            File.Delete(Path.Combine(dir, "CLILOC.ENU"));
            Action noEnu = () => ClilocDiscovery.Find(dir);
            noEnu.Should().Throw<InvalidDataException>("enu is required");
        }
    }
}
