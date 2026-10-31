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

    /// <summary>Extensions of files next to the cliloc files that aren't languages (exports, backups).</summary>
    private static readonly HashSet<string> NotLanguages = ["csv", "txt", "bak", "old", "tmp", "xml", "json"];

    /// <summary>The Cliloc.* files of the client folder: (language code, path), enu first.</summary>
    public static List<(string Lang, string Path)> ClilocFiles(UOFileManager fm)
    {
        var list = new List<(string, string)>();
        foreach (var p in Directory.EnumerateFiles(fm.BasePath, "cliloc.*"))
        {
            string ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
            if (ext.Length is < 1 or > 4 || !ext.All(char.IsAsciiLetterOrDigit) || NotLanguages.Contains(ext))
            {
                Console.WriteLine($"  cliloc: skipping '{Path.GetFileName(p)}' (not a 1-4 letter language code)");
                continue;
            }
            list.Add((ext, p));
        }
        return list.OrderBy(l => l.Item1 == "enu" ? "" : l.Item1, StringComparer.Ordinal).ToList();
    }

    public static string Cliloc(UOFileManager fm, string outDir)
    {
        var files = ClilocFiles(fm);
        var raws = new byte[files.Count][];
        var counts = new int[files.Count];
        Parallel.For(0, files.Count, ConvertOptions.Par, i =>
        {
            var entries = ClilocLoader.ReadFile(files[i].Path);
            counts[i] = entries.Count;
            raws[i] = ClilocTable.Build(entries.Select(e => (e.Key, Encoding.UTF8.GetBytes(e.Value))).ToList());
        });
        var w = new RecordStoreWriter(files.Count);
        long raw = 0, stored = 0;
        var parts = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            var (m0, m1) = ClilocTable.PackLanguage(files[i].Lang);
            int rec = AddRecord(w, raws[i], ClilocBrotli && !Raw ? StoreConst.CodecBrotli : StoreConst.CodecRaw, m0, m1, 0, ref stored);
            w.Slots[i] = new StoreSlot { Record = (uint)rec + 1 };
            raw += raws[i].Length;
            parts.Add($"{files[i].Lang} {counts[i]}");
        }
        var path = Path.Combine(outDir, TextConst.ClilocFile);
        w.Write(path, TextConst.MagicCliloc, 0);
        return $"{files.Count} languages ({string.Join(", ", parts)}), {raw / 1048576.0:F2} MiB -> {new FileInfo(path).Length / 1048576.0:F2} MiB";
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
        using (var ascii = RecordStore.Open(Path.Combine(outDir, TextConst.AsciiFontsFile), TextConst.MagicFontsAscii))
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
        using (var uni = RecordStore.Open(Path.Combine(outDir, TextConst.UnicodeFontsFile), TextConst.MagicFontsUnicode))
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
    /// Every cliloc id of every language: the pack's (language, then enu) lookup against the frozen ClilocLoader's
    /// Load(language) dictionary (enu, then the language file over it), without Clilocs.txt.
    /// </summary>
    public static void VerifyCliloc(UOFileManager fm, string outDir)
    {
        var sw = Stopwatch.StartNew();
        using var store = RecordStore.Open(Path.Combine(outDir, TextConst.ClilocFile), TextConst.MagicCliloc);
        var tables = new Dictionary<string, ClilocTable>();
        for (int i = 0; i < store.Records.Length; i++)
        {
            ref readonly var rec = ref store.Records[i];
            var lang = ClilocTable.UnpackLanguage(rec.Meta0, rec.Meta1);
            var raw = new byte[rec.RawBytes];
            store.Read(rec, raw);
            var t = ClilocTable.TryParse(raw);
            if (lang == null || t == null) { Fail($"cliloc record {i}: invalid"); continue; }
            tables[lang] = t;
        }
        int bad = 0;
        long ids = 0;
        var files = ClilocFiles(fm);
        if (files.Count != tables.Count) Fail($"cliloc: {tables.Count} languages in the pack, {files.Count} in the client folder");
        foreach (var (lang, _) in files)
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
        if (bad > 5) Fail($"cliloc: {bad} mismatches in total");
        Console.WriteLine($"  cliloc: {files.Count} languages, {ids} (language, id) strings {(bad == 0 ? "EXACT" : "MISMATCH")} vs ClilocLoader.Load ({sw.ElapsedMilliseconds} ms)");
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
