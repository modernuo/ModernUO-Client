using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Uo.Pack;

// Text in the pack (docs/modern-client/pack-format.md, "Text: fonts, cliloc, speech"):
//
//   text/fonts.bin     ASCII fonts (fonts.mul): a record store, one record per font
//   text/unifont.bin   unicode fonts (unifont*.mul): a record store, one record per 256-character page of a font
//   text/cliloc.bin    cliloc, every language and the shard's overrides: a record store (ClilocStore.cs)
//   text/speech.bin    speech.mul keywords: a plain table
//
// Every parser here treats its input as untrusted: offsets and lengths are bounds-checked, and a bad record is
// reported (false / null), never thrown, so the client can make it absent.

public static class TextConst
{
    public const uint MagicFontsAscii = 0x41544E46;   // "FNTA"
    public const uint MagicFontsUnicode = 0x55544E46; // "FNTU"
    public const uint MagicCliloc = 0x4C494C43;       // "CLIL"
    public const uint MagicSpeech = 0x48435053;       // "SPCH"

    public const string AsciiFontsFile = "text/fonts.bin";
    public const string UnicodeFontsFile = "text/unifont.bin";
    public const string ClilocFile = "text/cliloc.bin";
    public const string SpeechFile = "text/speech.bin";

    /// <summary>Glyphs per ASCII font (characters 32..255; the client maps 0..31 to glyph 0).</summary>
    public const int AsciiGlyphs = 224;
    /// <summary>Unicode font slots the client knows (unifont.mul, unifont1.mul .. unifont19.mul).</summary>
    public const int UnicodeFonts = 20;
    /// <summary>Characters per unicode page; a font has 256 pages (char >> 8).</summary>
    public const int PageChars = 256;
    public const int PagesPerFont = 256;
    public const int UnicodeSlots = UnicodeFonts * PagesPerFont;
    /// <summary>Most ASCII fonts a store may hold: the client addresses a font by a byte (7.0.103 has 10).</summary>
    public const int MaxAsciiFonts = 256;

    /// <summary>Opens text/fonts.bin: 1..<see cref="MaxAsciiFonts"/> slots, checked before the tables are allocated.</summary>
    public static RecordStore OpenAsciiStore(string path) => RecordStore.Open(path, MagicFontsAscii, 1, MaxAsciiFonts);

    /// <summary>Opens text/unifont.bin: exactly <see cref="UnicodeSlots"/> (20 fonts × 256 pages) slots.</summary>
    public static RecordStore OpenUnicodeStore(string path) => RecordStore.Open(path, MagicFontsUnicode, UnicodeSlots, UnicodeSlots);
}

// ---------------------------------------------------------------------------------------------------------------
// ASCII fonts
// ---------------------------------------------------------------------------------------------------------------

/// <summary>One ASCII glyph as the client holds it: width, height and texels (null = the client has no texel array).</summary>
public readonly record struct AsciiGlyph(byte Width, byte Height, ushort[]? Texels);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AsciiGlyphHeader
{
    public byte Width;
    public byte Height;
    /// <summary>Bit 0: the glyph has a texel array (Width·Height texels follow, possibly none).</summary>
    public byte Flags;
    public byte Pad;
}

/// <summary>
/// An ASCII font record: <c>AsciiGlyphHeader[224]</c>, then for every glyph with flag bit 0, <c>Width·Height</c> u16
/// texels (fonts.mul's 16-bit colours as stored), glyph after glyph. The header table is the size table.
/// </summary>
public static class AsciiFontCodec
{
    public const int HeaderBytes = TextConst.AsciiGlyphs * 4;
    /// <summary>Largest valid raw payload: every glyph 255×255.</summary>
    public const int MaxRawBytes = HeaderBytes + TextConst.AsciiGlyphs * 255 * 255 * 2;

    public static byte[] Encode(ReadOnlySpan<AsciiGlyph> glyphs)
    {
        if (glyphs.Length != TextConst.AsciiGlyphs) throw new ArgumentException("an ASCII font has 224 glyphs");
        int bytes = HeaderBytes;
        foreach (var g in glyphs)
        {
            if (g.Texels != null && g.Texels.Length != g.Width * g.Height) throw new ArgumentException("texel count");
            if (g.Texels == null && (g.Width != 0 || g.Height != 0)) throw new ArgumentException("a glyph without texels has no size");
            bytes += (g.Texels?.Length ?? 0) * 2;
        }
        var buf = new byte[bytes];
        int at = HeaderBytes;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var g = glyphs[i];
            buf[i * 4] = g.Width;
            buf[i * 4 + 1] = g.Height;
            buf[i * 4 + 2] = (byte)(g.Texels != null ? 1 : 0);
            if (g.Texels == null) continue;
            MemoryMarshal.AsBytes(g.Texels.AsSpan()).CopyTo(buf.AsSpan(at));
            at += g.Texels.Length * 2;
        }
        return buf;
    }

    /// <summary>Raw size implied by the header table, or -1 when the table is malformed (unknown flags, padding set, a size without texels).</summary>
    public static long RawSize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes) return -1;
        long bytes = HeaderBytes;
        for (int i = 0; i < TextConst.AsciiGlyphs; i++)
        {
            byte flags = payload[i * 4 + 2];
            if ((flags & ~1) != 0 || payload[i * 4 + 3] != 0) return -1;
            if ((flags & 1) != 0) bytes += payload[i * 4] * payload[i * 4 + 1] * 2;
            // a glyph without texels has no size (the client only has those for glyphs past the end of fonts.mul, 0×0);
            // a positive size without texels would make the renderer index a null array
            else if (payload[i * 4] != 0 || payload[i * 4 + 1] != 0) return -1;
        }
        return bytes;
    }

    /// <summary>Decodes a whole font into <paramref name="dst"/> (224 entries); false when the payload is malformed.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, Span<AsciiGlyph> dst)
    {
        if (dst.Length < TextConst.AsciiGlyphs || RawSize(payload) != payload.Length) return false;
        int at = HeaderBytes;
        for (int i = 0; i < TextConst.AsciiGlyphs; i++)
        {
            byte w = payload[i * 4], h = payload[i * 4 + 1];
            if ((payload[i * 4 + 2] & 1) == 0)
            {
                dst[i] = new AsciiGlyph(w, h, null);
                continue;
            }
            var texels = new ushort[w * h];
            payload.Slice(at, texels.Length * 2).CopyTo(MemoryMarshal.AsBytes(texels.AsSpan()));
            at += texels.Length * 2;
            dst[i] = new AsciiGlyph(w, h, texels);
        }
        return true;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Unicode fonts
// ---------------------------------------------------------------------------------------------------------------

/// <summary>One unicode glyph as the client draws it: offsets, size and the 1-bpp rows (null = no glyph).</summary>
public readonly record struct UnicodeGlyph(sbyte OffsetX, sbyte OffsetY, sbyte Width, sbyte Height, byte[]? Bits)
{
    /// <summary>Bytes of a glyph's 1-bpp bitmap: one row is ((width − 1) / 8 + 1) bytes, MSB first.</summary>
    public static int BitBytes(int width, int height) => width > 0 && height > 0 ? ((width - 1) / 8 + 1) * height : 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UnicodeGlyphHeader
{
    public sbyte OffsetX, OffsetY, Width, Height;
    /// <summary>Start of the glyph's rows after the header table; <see cref="UnicodePageCodec.NoGlyph"/> = no glyph.</summary>
    public uint DataOffset;
}

/// <summary>
/// A unicode page record (256 characters of one font): <c>UnicodeGlyphHeader[256]</c> (the page's size table:
/// offsets and sizes of every glyph, 2 KiB), then the glyphs' 1-bpp rows back to back, as unifont*.mul stores them.
/// A glyph exists when its width and height are positive; others have every field 0 and DataOffset = NoGlyph.
/// </summary>
public static class UnicodePageCodec
{
    public const uint NoGlyph = 0xFFFFFFFF;
    public const int HeaderBytes = TextConst.PageChars * 8;
    /// <summary>Largest valid raw payload: 256 glyphs of 127×127.</summary>
    public const int MaxRawBytes = HeaderBytes + TextConst.PageChars * 16 * 127;

    public static byte[] Encode(ReadOnlySpan<UnicodeGlyph> glyphs)
    {
        if (glyphs.Length != TextConst.PageChars) throw new ArgumentException("a page has 256 glyphs");
        int bytes = HeaderBytes;
        foreach (var g in glyphs)
            if (g.Bits != null) bytes += g.Bits.Length;
        var buf = new byte[bytes];
        var headers = MemoryMarshal.Cast<byte, UnicodeGlyphHeader>(buf.AsSpan(0, HeaderBytes));
        uint at = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var g = glyphs[i];
            if (g.Bits == null)
            {
                headers[i] = new UnicodeGlyphHeader { DataOffset = NoGlyph };
                continue;
            }
            if (g.Width <= 0 || g.Height <= 0 || g.Bits.Length != UnicodeGlyph.BitBytes(g.Width, g.Height)) throw new ArgumentException("glyph size");
            headers[i] = new UnicodeGlyphHeader { OffsetX = g.OffsetX, OffsetY = g.OffsetY, Width = g.Width, Height = g.Height, DataOffset = at };
            g.Bits.CopyTo(buf.AsSpan(HeaderBytes + (int)at));
            at += (uint)g.Bits.Length;
        }
        return buf;
    }

    /// <summary>
    /// Checks a page payload: every glyph's rows lie inside the payload, absent glyphs are all zero. Returns the number
    /// of glyphs, or -1 when malformed.
    /// </summary>
    public static int Validate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes || payload.Length > MaxRawBytes) return -1;
        var headers = MemoryMarshal.Cast<byte, UnicodeGlyphHeader>(payload.Slice(0, HeaderBytes));
        long bitsLength = payload.Length - HeaderBytes;
        int count = 0;
        foreach (ref readonly var h in headers)
        {
            if (h.DataOffset == NoGlyph)
            {
                if (h.OffsetX != 0 || h.OffsetY != 0 || h.Width != 0 || h.Height != 0) return -1;
                continue;
            }
            if (h.Width <= 0 || h.Height <= 0) return -1;
            if (h.DataOffset > bitsLength || UnicodeGlyph.BitBytes(h.Width, h.Height) > bitsLength - h.DataOffset) return -1;
            count++;
        }
        return count;
    }

    /// <summary>The header of glyph <paramref name="index"/> (0..255) in a validated payload.</summary>
    public static UnicodeGlyphHeader Header(ReadOnlySpan<byte> payload, int index) =>
        MemoryMarshal.Read<UnicodeGlyphHeader>(payload.Slice(index * 8, 8));

    /// <summary>The 1-bpp rows of a present glyph in a validated payload.</summary>
    public static ReadOnlySpan<byte> Bits(ReadOnlySpan<byte> payload, in UnicodeGlyphHeader h) =>
        payload.Slice(HeaderBytes + (int)h.DataOffset, UnicodeGlyph.BitBytes(h.Width, h.Height));
}

// ---------------------------------------------------------------------------------------------------------------
// Speech
// ---------------------------------------------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SpeechRecord
{
    public ushort Id;
    public ushort Pad;
    /// <summary>End of this entry's text in the blob; the text starts at the previous entry's end (0 for the first).</summary>
    public uint End;
}

/// <summary>
/// text/speech.bin: <c>FileHeader</c>, count u32, blobBytes u32, <c>SpeechRecord[count]</c>, then the UTF-8 blob.
/// The entries of speech.mul with a non-empty text, in file order (the client's keyword matching iterates them in
/// that order and sorts its result by id).
/// </summary>
public static class SpeechFile
{
    public const int MaxEntries = 1 << 20;
    public const int MaxBlobBytes = 64 << 20;

    public static void Write(string path, IReadOnlyList<(ushort Id, byte[] Utf8)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, TextConst.MagicSpeech);
        long blob = entries.Sum(e => (long)e.Utf8.Length);
        StructIo.Write(fs, (uint)entries.Count);
        StructIo.Write(fs, checked((uint)blob));
        uint end = 0;
        foreach (var e in entries)
        {
            end += (uint)e.Utf8.Length;
            var r = new SpeechRecord { Id = e.Id, End = end };
            StructIo.Write(fs, in r);
        }
        foreach (var e in entries) fs.Write(e.Utf8);
    }

    /// <summary>
    /// Reads every entry (id, text). Throws <see cref="InvalidDataException"/> when the file is not a valid speech
    /// table (bad header, sizes that don't add up, ends out of order); the client then falls back to speech.mul.
    /// </summary>
    public static (ushort Id, string Text)[] Read(string path)
    {
        // the fixed header first: nothing is allocated for the table until its sizes are known to be bounded and to
        // match the file's exact length (an oversized or forged file must not be read whole)
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        int hdr = StructIo.SizeOf<FileHeader>();
        Span<byte> head = stackalloc byte[hdr + 8];
        long length = fs.Length;
        if (length < head.Length) throw new InvalidDataException($"'{path}' is too short");
        fs.ReadExactly(head);
        var fh = MemoryMarshal.Read<FileHeader>(head);
        if (fh.Magic != TextConst.MagicSpeech || !PackConst.VersionSupported(fh.Version) || fh.Kind != FileKind.Raw)
            throw new InvalidDataException($"'{path}' is not a speech table");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(hdr));
        uint blob = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(hdr + 4));
        if (count > MaxEntries || blob > MaxBlobBytes || (ulong)hdr + 8 + count * 8UL + blob != (ulong)length)
            throw new InvalidDataException($"'{path}': bad sizes");
        // bounded by the caps above: at most 8 MiB of records plus 64 MiB of text
        var bytes = new byte[length];
        head.CopyTo(bytes);
        fs.ReadExactly(bytes.AsSpan(head.Length));
        var records = MemoryMarshal.Cast<byte, SpeechRecord>(bytes.AsSpan(hdr + 8, (int)count * 8));
        int blobAt = hdr + 8 + (int)count * 8;
        var result = new (ushort, string)[count];
        uint start = 0;
        for (int i = 0; i < records.Length; i++)
        {
            uint end = records[i].End;
            if (end < start || end > blob) throw new InvalidDataException($"'{path}': entry {i} out of range");
            result[i] = (records[i].Id, Encoding.UTF8.GetString(bytes, blobAt + (int)start, (int)(end - start)));
            start = end;
        }
        if (start != blob) throw new InvalidDataException($"'{path}': blob size mismatch");
        return result;
    }
}
