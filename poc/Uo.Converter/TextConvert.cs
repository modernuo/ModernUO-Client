using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ClassicUO.Assets;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// text/ (docs/modern-client/pack-format.md, "Text: fonts, cliloc, speech"): ASCII and unicode fonts, cliloc in every
/// language the client folder has, and speech.mul's keywords, read through the frozen loaders in
/// tools/ClassicUO.Assets.Mul so the pack holds exactly what the client resolved.
/// </summary>
public static class TextConvert
{
    /// <summary>--raw-text: store every record uncompressed (codec 0).</summary>
    public static bool Raw;
    /// <summary>--text-small n: a record whose raw payload is at most n bytes is stored uncompressed (Brotli's fixed decode cost dominates small payloads).</summary>
    public static int SmallRaw = 16384;
    /// <summary>--cliloc-brotli: Brotli for cliloc language records too (ship: raw, one read without a decode at startup).</summary>
    public static bool ClilocBrotli;
    public const int Quality = 9;

    private static ushort CodecFor(int rawBytes) => Raw || rawBytes <= SmallRaw ? StoreConst.CodecRaw : StoreConst.CodecBrotli;

    private static int AddRecord(RecordStoreWriter w, byte[] raw, ushort codec, ushort m0, ushort m1, ushort m2, ref long stored)
    {
        var payload = RecordStoreWriter.Encode(raw, codec, Quality);
        if (codec == StoreConst.CodecBrotli && payload.Length >= raw.Length)
        {
            codec = StoreConst.CodecRaw; // incompressible: keep it raw
            payload = raw;
        }
        stored += payload.Length;
        return w.Add(payload, (uint)raw.Length, codec, m0, m1, m2);
    }

    // ---------------------------------------------------------------- fonts

    public static AsciiGlyph[] AsciiFont(FontsLoader f, int font)
    {
        var glyphs = new AsciiGlyph[TextConst.AsciiGlyphs];
        for (int j = 0; j < glyphs.Length; j++)
        {
            ref var g = ref f.GetAscii(font, j);
            glyphs[j] = new AsciiGlyph(g.Width, g.Height, g.Data);
        }
        return glyphs;
    }

    /// <summary>The 256 glyphs of a page as the client draws them (no glyph = Bits null).</summary>
    public static UnicodeGlyph[] UnicodePage(FontsLoader f, int font, int page)
    {
        var glyphs = new UnicodeGlyph[TextConst.PageChars];
        for (int i = 0; i < glyphs.Length; i++)
        {
            char c = (char)(page * TextConst.PageChars + i);
            try
            {
                ref var g = ref f.GetCharUni((byte)font, c);
                glyphs[i] = g.Data == null ? default : new UnicodeGlyph(g.OffsetX, g.OffsetY, g.Width, g.Height, g.Data);
            }
            catch (EndOfStreamException)
            {
                // the glyph's lookup or rows run past the end of the unifont file: the client throws when it draws this
                // character. The pack stores no glyph (the client's MUL path now does the same).
                lock (Broken) Broken.Add((font, c));
                glyphs[i] = default;
            }
        }
        return glyphs;
    }

    /// <summary>Unicode glyphs the client's loader throws on (the file ends inside them): stored as no glyph.</summary>
    public static readonly HashSet<(int Font, char C)> Broken = new();

    public static string Fonts(UOFileManager fm, string outDir)
    {
        var f = fm.Fonts;
        f.Load();

        // ASCII: one record per font
        var ascii = new RecordStoreWriter(f.FontCount);
        long aRaw = 0, aStored = 0;
        for (int font = 0; font < f.FontCount; font++)
        {
            var raw = AsciiFontCodec.Encode(AsciiFont(f, font));
            aRaw += raw.Length;
            int rec = AddRecord(ascii, raw, CodecFor(raw.Length), TextConst.AsciiGlyphs, 0, 0, ref aStored);
            ascii.Slots[font] = new StoreSlot { Record = (uint)rec + 1 };
        }
        var aPath = Path.Combine(outDir, TextConst.AsciiFontsFile);
        ascii.Write(aPath, TextConst.MagicFontsAscii, 0);

        // Unicode: one record per non-empty 256-character page; identical pages (unifont1 = unifont when missing,
        // and fonts that share pages) share one record.
        uint exists = 0;
        for (int font = 0; font < TextConst.UnicodeFonts; font++)
            if (f.UnicodeFontExists((byte)font)) exists |= 1u << font;
        var pages = new byte[TextConst.UnicodeSlots][];
        var counts = new int[TextConst.UnicodeSlots];
        // GetCharUni reads through a seekable UOFile: one thread
        for (int font = 0; font < TextConst.UnicodeFonts; font++)
        {
            if ((exists & (1u << font)) == 0) continue;
            for (int page = 0; page < TextConst.PagesPerFont; page++)
            {
                var glyphs = UnicodePage(f, font, page);
                int n = glyphs.Count(g => g.Bits != null);
                if (n == 0) continue;
                pages[font * TextConst.PagesPerFont + page] = UnicodePageCodec.Encode(glyphs);
                counts[font * TextConst.PagesPerFont + page] = n;
            }
        }
        var uni = new RecordStoreWriter(TextConst.UnicodeSlots);
        var byContent = new Dictionary<string, int>();
        long uRaw = 0, uStored = 0;
        int records = 0, shared = 0, glyphTotal = 0, brotli = 0;
        var encoded = new (byte[] Payload, ushort Codec)[pages.Length];
        Parallel.For(0, pages.Length, ConvertOptions.Par, i =>
        {
            if (pages[i] == null) return;
            ushort codec = CodecFor(pages[i].Length);
            var p = RecordStoreWriter.Encode(pages[i], codec, Quality);
            encoded[i] = codec == StoreConst.CodecBrotli && p.Length >= pages[i].Length ? (pages[i], StoreConst.CodecRaw) : (p, codec);
        });
        for (int slot = 0; slot < pages.Length; slot++)
        {
            var raw = pages[slot];
            if (raw == null) continue;
            glyphTotal += counts[slot];
            var key = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(raw));
            if (!byContent.TryGetValue(key, out int rec))
            {
                var (payload, codec) = encoded[slot];
                if (codec == StoreConst.CodecBrotli) brotli++;
                rec = uni.Add(payload, (uint)raw.Length, codec, (ushort)(slot / TextConst.PagesPerFont), (ushort)(slot % TextConst.PagesPerFont), (ushort)counts[slot]);
                byContent[key] = rec;
                uRaw += raw.Length;
                uStored += payload.Length;
                records++;
            }
            else shared++;
            uni.Slots[slot] = new StoreSlot { Record = (uint)rec + 1 };
        }
        var uPath = Path.Combine(outDir, TextConst.UnicodeFontsFile);
        uni.Write(uPath, TextConst.MagicFontsUnicode, exists);
        return $"ascii: {f.FontCount} fonts, {aRaw / 1024.0:F0} KiB -> {new FileInfo(aPath).Length / 1024.0:F0} KiB; " +
               $"unicode: fonts 0x{exists:X5}, {glyphTotal} glyphs in {records} page records ({shared} pages shared, {brotli} Brotli), " +
               $"{uRaw / 1048576.0:F2} MiB -> {new FileInfo(uPath).Length / 1048576.0:F2} MiB" +
               (Broken.Count > 0 ? $"; {Broken.Count} glyphs past the end of their file stored as none: {string.Join(", ", Broken.Take(12).Select(b => $"font {b.Font} U+{(int)b.C:X4}"))}" : "");
    }

    // ---------------------------------------------------------------- cliloc

    /// <summary>--cliloc-skip a,b: extensions of Cliloc.* files that are not languages, in addition to those found by content.</summary>
    public static HashSet<string> ClilocSkip = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>--no-cliloc-overrides: don't fold the client folder's Clilocs.txt into text/cliloc.bin.</summary>
    public static bool ClilocOverridesOff;

    /// <summary>The Cliloc.* language files of the client folder (<see cref="ClilocDiscovery.Find"/>), enu first; throws when none is left.</summary>
    public static List<(string Lang, string Path)> ClilocFiles(string folder) =>
        ClilocDiscovery.Find(folder, ClilocSkip, m => Console.WriteLine($"  cliloc: {m}"));

    public static string Cliloc(UOFileManager fm, string outDir)
    {
        var files = ClilocFiles(fm.BasePath);
        var raws = new byte[files.Count][];
        var counts = new int[files.Count];
        Parallel.For(0, files.Count, ConvertOptions.Par, i =>
        {
            var entries = ClilocLoader.ReadFile(files[i].Path);
            counts[i] = entries.Count;
            raws[i] = ClilocTable.Build(entries.Select(e => (e.Key, Encoding.UTF8.GetBytes(e.Value))).ToList());
        });
        var ours = OverridesFile(fm.BasePath);
        var w = new RecordStoreWriter(files.Count + (ours != null ? 1 : 0));
        long raw = 0, stored = 0;
        var parts = new List<string>();
        // flat records, stored raw unless --cliloc-brotli (whole-record Brotli, opt-in)
        ushort codec = ClilocBrotli && !Raw ? StoreConst.CodecBrotli : StoreConst.CodecRaw;
        for (int i = 0; i < files.Count; i++)
        {
            var (m0, m1) = ClilocTable.PackLanguage(files[i].Lang);
            int rec = AddRecord(w, raws[i], codec, m0, m1, ClilocStore.KindLanguage, ref stored);
            w.Slots[i] = new StoreSlot { Record = (uint)rec + 1 };
            raw += raws[i].Length;
            parts.Add($"{files[i].Lang} {counts[i]}");
        }
        if (ours != null)
        {
            // the shard's own strings: a flat record of their own, after the languages
            var payload = ClilocTable.Build(ours.Select(e => (e.Key, Encoding.UTF8.GetBytes(e.Value))).ToList());
            int rec = AddRecord(w, payload, codec, 0, 0, ClilocStore.KindOverrides, ref stored);
            w.Slots[files.Count] = new StoreSlot { Record = (uint)rec + 1 };
            raw += payload.Length;
            parts.Add($"overrides {ours.Count}");
        }
        var path = Path.Combine(outDir, TextConst.ClilocFile);
        w.Write(path, TextConst.MagicCliloc, 0);
        return $"{files.Count} languages ({string.Join(", ", parts)}), {raw / 1048576.0:F2} MiB -> {new FileInfo(path).Length / 1048576.0:F2} MiB";
    }

    /// <summary>The client folder's Clilocs.txt (the shard's own strings), or null when there is none or it is switched off.</summary>
    public static Dictionary<int, string>? OverridesFile(string folder)
    {
        if (ClilocOverridesOff) return null;
        var p = Directory.EnumerateFiles(folder).FirstOrDefault(f => string.Equals(Path.GetFileName(f), ClilocOverrides.FileName, StringComparison.OrdinalIgnoreCase));
        return p == null ? null : ClilocOverrides.Parse(File.ReadLines(p));
    }

    // ---------------------------------------------------------------- speech

    public static string Speech(UOFileManager fm, string outDir)
    {
        var s = fm.Speeches;
        s.Texts.Clear();
        s.Load();
        var path = Path.Combine(outDir, TextConst.SpeechFile);
        SpeechFile.Write(path, s.Texts.Select(t => (t.Id, Encoding.UTF8.GetBytes(t.Text))).ToList());
        return $"{s.Texts.Count} entries, {new FileInfo(path).Length / 1024.0:F0} KiB";
    }

    // ---------------------------------------------------------------- verify

    private static void Fail(string m) => Verify.Fail(m);

    /// <summary>Every ASCII glyph and every unicode glyph of every font slot (20 × 65,536) against the frozen FontsLoader.</summary>
    public static void VerifyFonts(UOFileManager fm, string outDir)
    {
        var f = fm.Fonts;
        if (f.FontCount == 0) f.Load();
        var sw = Stopwatch.StartNew();
        int bad = 0;
        using (var ascii = TextConst.OpenAsciiStore(Path.Combine(outDir, TextConst.AsciiFontsFile)))
        {
            if (ascii.SlotCount != f.FontCount) Fail($"fonts: {ascii.SlotCount} ASCII fonts, the client has {f.FontCount}");
            var dst = new AsciiGlyph[TextConst.AsciiGlyphs];
            for (int font = 0; font < Math.Min(ascii.SlotCount, f.FontCount); font++)
            {
                if (!ascii.TryGet(font, out _, out var rec)) { if (bad++ < 5) Fail($"font {font}: missing"); continue; }
                var raw = new byte[rec.RawBytes];
                ascii.Read(rec, raw);
                if (!AsciiFontCodec.TryDecode(raw, dst)) { if (bad++ < 5) Fail($"font {font}: decode"); continue; }
                var want = AsciiFont(f, font);
                for (int j = 0; j < dst.Length; j++)
                {
                    var a = dst[j];
                    var b = want[j];
                    if (a.Width != b.Width || a.Height != b.Height || (a.Texels == null) != (b.Texels == null) ||
                        (a.Texels != null && !a.Texels.AsSpan().SequenceEqual(b.Texels)))
                        if (bad++ < 5) Fail($"font {font} glyph {j}");
                }
            }
        }
        long glyphs = 0;
        using (var uni = TextConst.OpenUnicodeStore(Path.Combine(outDir, TextConst.UnicodeFontsFile)))
        {
            for (int font = 0; font < TextConst.UnicodeFonts; font++)
            {
                bool exists = (uni.Header.Flags & (1u << font)) != 0;
                if (exists != f.UnicodeFontExists((byte)font)) if (bad++ < 5) Fail($"unifont {font}: exists flag");
                for (int page = 0; page < TextConst.PagesPerFont; page++)
                {
                    var want = UnicodePage(f, font, page);
                    byte[]? raw = null;
                    if (uni.TryGet(font * TextConst.PagesPerFont + page, out _, out var rec))
                    {
                        raw = new byte[rec.RawBytes];
                        uni.Read(rec, raw);
                        if (UnicodePageCodec.Validate(raw) != rec.Meta2) { if (bad++ < 5) Fail($"unifont {font} page {page}: invalid"); continue; }
                    }
                    for (int i = 0; i < TextConst.PageChars; i++)
                    {
                        var w = want[i];
                        UnicodeGlyph got = default;
                        if (raw != null)
                        {
                            var h = UnicodePageCodec.Header(raw, i);
                            if (h.DataOffset != UnicodePageCodec.NoGlyph)
                                got = new UnicodeGlyph(h.OffsetX, h.OffsetY, h.Width, h.Height, UnicodePageCodec.Bits(raw, h).ToArray());
                        }
                        if (w.Bits != null) glyphs++;
                        if ((got.Bits == null) != (w.Bits == null) ||
                            (w.Bits != null && (got.OffsetX != w.OffsetX || got.OffsetY != w.OffsetY || got.Width != w.Width || got.Height != w.Height || !got.Bits.AsSpan().SequenceEqual(w.Bits))))
                            if (bad++ < 5) Fail($"unifont {font} char 0x{page * 256 + i:X4}");
                    }
                }
            }
        }
        if (bad > 5) Fail($"fonts: {bad} mismatches in total");
        Console.WriteLine($"  fonts: {f.FontCount} ASCII fonts × 224 glyphs and 20 unicode fonts × 65,536 chars ({glyphs} glyphs) {(bad == 0 ? "EXACT" : "MISMATCH")} vs FontsLoader ({sw.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// Every cliloc id of every language, resolved through the client's slot rules (<see cref="ClilocStore.Resolve"/>:
    /// slot order, duplicate codes rejected) and <see cref="ClilocStore.ReadTable"/>: the pack's (language, then enu)
    /// lookup against the frozen ClilocLoader's Load(language) dictionary (enu, then the language file over it), without
    /// Clilocs.txt; the overrides record against Clilocs.txt; and the language set against the folder, discovered again
    /// independently (a case-insensitive listing, each file walked with a BinaryReader and read by the frozen loader).
    /// </summary>
    public static void VerifyCliloc(UOFileManager fm, string outDir)
    {
        var sw = Stopwatch.StartNew();
        using var store = RecordStore.Open(Path.Combine(outDir, TextConst.ClilocFile), TextConst.MagicCliloc);
        ClilocDirectory dir;
        try
        {
            dir = ClilocStore.Resolve(store);
        }
        catch (InvalidDataException e)
        {
            Fail($"cliloc: the store does not resolve ({e.Message})");
            return;
        }
        var tables = new Dictionary<string, ClilocTable>();
        foreach (var (lang, rec) in dir.Languages)
        {
            var t = ClilocStore.ReadTable(store, rec, out var why);
            if (t == null) { Fail($"cliloc {lang}: record {rec} has {why}"); continue; }
            tables[lang] = t;
        }
        if (dir.Order.Count == 0) Fail("cliloc: no languages in the pack");
        if (!dir.Languages.ContainsKey("enu")) Fail("cliloc: the pack has no enu");

        // the language set, discovered again without ClilocFiles
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in Directory.EnumerateFiles(fm.BasePath, "cliloc.*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
        {
            string ext = Path.GetFileName(p).Substring("cliloc.".Length).ToLowerInvariant();
            if (ext.Length is < 1 or > 4 || !ext.All(char.IsAsciiLetterOrDigit) || ClilocSkip.Contains(ext)) continue;
            if (WalkCliloc(p) && ClilocLoader.ReadFile(p).Count > 0) expected.Add(ext);
        }
        if (expected.Count == 0) Fail("cliloc: no language files found in the client folder");
        if (!expected.SetEquals(dir.Languages.Keys))
            Fail($"cliloc: the pack has [{string.Join(",", dir.Order)}], the client folder [{string.Join(",", expected)}]");

        int bad = 0;
        long ids = 0;
        foreach (var lang in expected)
        {
            var loader = new ClilocLoader(fm) { IncludeOurs = false };
            loader.Load(lang);
            var want = loader.Entries;
            if (!tables.TryGetValue(lang, out var t)) { Fail($"cliloc {lang}: missing"); continue; }
            tables.TryGetValue("enu", out var enu);
            // every id the client resolves, and every id the pack resolves
            foreach (var (id, text) in want)
            {
                ids++;
                string? got = t.TryGetString(id, out var s) ? s : enu != null && enu.TryGetString(id, out s) ? s : null;
                if (got != text) if (bad++ < 5) Fail($"cliloc {lang} {id}: '{got}' vs '{text}'");
            }
            foreach (var src in enu != null && lang != "enu" ? new[] { t, enu } : new[] { t })
                for (int i = 0; i < src.Count; i++)
                    if (!want.ContainsKey(src.IdAt(i)) && bad++ < 5) Fail($"cliloc {lang}: extra id {src.IdAt(i)}");
            loader.Dispose();
        }

        // the shard's own strings
        var ours = OverridesFile(fm.BasePath);
        int oursChecked = 0;
        if ((ours != null) != (dir.Overrides >= 0)) Fail($"cliloc: overrides record {(dir.Overrides >= 0 ? "present" : "absent")}, Clilocs.txt {(ours != null ? "present" : "absent")}");
        else if (ours != null)
        {
            var o = ClilocStore.ReadTable(store, dir.Overrides, out var why);
            if (o == null) Fail($"cliloc overrides: {why}");
            else
            {
                if (o.Count != ours.Count) Fail($"cliloc overrides: {o.Count} strings, Clilocs.txt has {ours.Count}");
                foreach (var (id, text) in ours)
                {
                    oursChecked++;
                    if (!o.TryGetString(id, out var s) || s != text) if (bad++ < 5) Fail($"cliloc overrides {id}: '{s}' vs '{text}'");
                }
            }
        }
        if (bad > 5) Fail($"cliloc: {bad} mismatches in total");
        Console.WriteLine($"  cliloc: {expected.Count} languages ({string.Join(",", dir.Order)}), {ids} (language, id) strings{(ours != null ? $" and {oursChecked} overrides" : "")} {(bad == 0 ? "EXACT" : "MISMATCH")} vs ClilocLoader.Load ({sw.ElapsedMilliseconds} ms)");
    }

    // The discovery rule walked independently of IsClilocFile: BWT-compressed, or a raw table read record by record
    // that ends exactly at the end of the file.
    private static bool WalkCliloc(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        long n = r.BaseStream.Length;
        if (n < 6) return false;
        var head = r.ReadBytes(6);
        if (head[3] == 0x8E) return true;
        int records = 0;
        while (r.BaseStream.Position < n)
        {
            if (n - r.BaseStream.Position < 7) return false;
            r.ReadInt32();
            r.ReadByte();
            ushort len = r.ReadUInt16();
            if (len > n - r.BaseStream.Position) return false;
            r.BaseStream.Seek(len, SeekOrigin.Current);
            records++;
        }
        return records > 0;
    }

    /// <summary>Every speech entry, in order: id and text, against the frozen SpeechesLoader.</summary>
    public static void VerifySpeech(UOFileManager fm, string outDir)
    {
        var s = fm.Speeches;
        if (s.Texts.Count == 0) s.Load();
        var got = SpeechFile.Read(Path.Combine(outDir, TextConst.SpeechFile));
        int bad = got.Length == s.Texts.Count ? 0 : 1;
        if (bad != 0) Fail($"speech: {got.Length} entries, the client has {s.Texts.Count}");
        for (int i = 0; i < Math.Min(got.Length, s.Texts.Count); i++)
        {
            var e = s.Entries[i];
            var g = new SpeechEntry(got[i].Id, got[i].Text);
            if (got[i] != s.Texts[i] || g.KeywordID != e.KeywordID || g.CheckStart != e.CheckStart || g.CheckEnd != e.CheckEnd || !g.Keywords.SequenceEqual(e.Keywords))
                if (bad++ < 5) Fail($"speech entry {i} (id {s.Texts[i].Id})");
        }
        Console.WriteLine($"  speech: {got.Length} entries (id, text, keywords, start/end flags) {(bad == 0 ? "EXACT" : "MISMATCH")} vs SpeechesLoader");
    }
}
