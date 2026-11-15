using System.Buffers.Binary;

namespace Uo.Pack;

/// <summary>
/// The gump record payload (docs/modern-client/pack-format.md, "Gumps"), one of two layouts (StoreRecord.Meta2):
/// <see cref="LayoutTexels"/>, w·h B5G5R5A1 texels (0 = transparent, else bit 15 set and bits 0-14 the colour), or
/// <see cref="LayoutRle"/>, run-length rows of the same texels:
/// <code>
/// u32 rowStart[h]        row y's first run, in 4-byte units from the start of this table
/// (u16 texel, u16 run)*  runs of one texel; texel 0 = transparent, else bit 15 set and bits 0-14 the colour
/// </code>
/// Row y's runs end where row y+1's start (the last row: at the end of the payload). Texels a row's runs don't reach
/// are transparent. This is the layout of the client's own gump entries, so the decode is the client's walk; the
/// converter re-encodes every gump canonically (one run per change of texel, no trailing transparent run).
/// </summary>
public static class GumpCodec
{
    /// <summary>StoreRecord.Meta2 of a gump: w·h B5G5R5A1 texels, row-major (the ship profile, with Brotli).</summary>
    public const ushort LayoutTexels = 0;
    /// <summary>StoreRecord.Meta2 of a gump: run-length rows (<see cref="EncodeRle"/>; --raw-gumps, stored uncompressed).</summary>
    public const ushort LayoutRle = 1;

    /// <summary>The payload of a gump stored with <paramref name="layout"/>.</summary>
    public static byte[] Encode(ushort layout, ReadOnlySpan<ushort> texels, int w, int h) => layout switch
    {
        LayoutTexels => System.Runtime.InteropServices.MemoryMarshal.AsBytes(texels.Slice(0, w * h)).ToArray(),
        LayoutRle => EncodeRle(texels, w, h),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>
    /// A decoded gump payload of either layout to RGBA8 as the client draws it (see the RGBA <see cref="DecodeRle(ReadOnlySpan{byte}, int, int, Span{uint}, ReadOnlySpan{uint}, ReadOnlySpan{ushort})"/>
    /// for the hue rule). False on malformed data.
    /// </summary>
    public static bool Decode(ushort layout, ReadOnlySpan<byte> payload, int w, int h, Span<uint> dst, uint[] lut, ReadOnlySpan<uint> expand, ReadOnlySpan<ushort> hueShades)
    {
        if (layout == LayoutRle) return DecodeRle(payload, w, h, dst, expand, hueShades);
        if (layout != LayoutTexels || w <= 0 || h <= 0 || payload.Length != w * h * 2 || dst.Length < w * h) return false;
        ExpandTexels(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(payload), dst, lut, hueShades);
        return true;
    }

    /// <summary>
    /// B5G5R5A1 texels to RGBA8: through <paramref name="lut"/> (<see cref="BuildLut"/>), or with the alias hue as the
    /// client applies it. <paramref name="src"/> may be the upper half of <paramref name="dst"/>'s own memory (texel i at
    /// byte 2·n + 2i of a buffer whose RGBA texel i is at byte 4i): each texel is read before anything overwrites it.
    /// </summary>
    public static void ExpandTexels(ReadOnlySpan<ushort> src, Span<uint> dst, uint[] lut, ReadOnlySpan<ushort> hueShades)
    {
        dst = dst.Slice(0, src.Length);
        if (hueShades.Length < 32)
        {
            for (int i = 0; i < src.Length; i++) dst[i] = lut[src[i]];
            return;
        }
        for (int i = 0; i < src.Length; i++)
        {
            ushort v = src[i];
            if (v != 0) v = hueShades[(v >> 10) & 0x1F];
            dst[i] = lut[v == 0 ? 0 : v | 0x8000];
        }
    }

    /// <summary>A 65,536-entry B5G5R5A1 -> RGBA8 table (texel 0 -> 0, else the expanded colour with alpha 255).</summary>
    public static uint[] BuildLut(ReadOnlySpan<uint> expand)
    {
        var lut = new uint[65536];
        for (int v = 1; v < lut.Length; v++)
            lut[v] = expand[(v >> 10) & 0x1F] | expand[(v >> 5) & 0x1F] << 8 | expand[v & 0x1F] << 16 | 0xFF000000;
        return lut;
    }

    public static byte[] EncodeRle(ReadOnlySpan<ushort> texels, int w, int h)
    {
        var ms = new MemoryStream(h * 4 + 64);
        Span<byte> b4 = stackalloc byte[4];
        ms.SetLength(h * 4L);
        ms.Position = h * 4L;
        var table = new uint[h];
        for (int y = 0; y < h; y++)
        {
            table[y] = (uint)(ms.Position / 4);
            var row = texels.Slice(y * w, w);
            int end = w;
            while (end > 0 && row[end - 1] == 0) end--;
            int x = 0;
            while (x < end)
            {
                ushort v = row[x];
                int run = 1;
                while (x + run < end && row[x + run] == v && run < ushort.MaxValue) run++;
                BinaryPrimitives.WriteUInt16LittleEndian(b4, v);
                BinaryPrimitives.WriteUInt16LittleEndian(b4[2..], (ushort)run);
                ms.Write(b4);
                x += run;
            }
        }
        var bytes = ms.ToArray();
        for (int y = 0; y < h; y++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(y * 4), table[y]);
        return bytes;
    }

    /// <summary>Decodes into 16-bit texels; every texel of <paramref name="dst"/> (w·h) is written. False on malformed data.</summary>
    public static bool DecodeRle(ReadOnlySpan<byte> src, int w, int h, Span<ushort> dst)
    {
        int words = src.Length >> 2;
        if (h <= 0 || w <= 0 || words < h || dst.Length < w * h) return false;
        for (int y = 0; y < h; y++)
        {
            int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(y * 4));
            int end = y < h - 1 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice((y + 1) * 4)) : words;
            if (start < h || end < start || end > words) return false;
            var row = dst.Slice(y * w, w);
            int x = 0;
            for (int i = start; i < end; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 4));
                int run = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 4 + 2)), w - x);
                row.Slice(x, run).Fill(v);
                x += run;
            }
            row.Slice(x).Clear();
        }
        return true;
    }

    /// <summary>
    /// Decodes into RGBA8 texels as the client draws them. <paramref name="expand"/> maps a 5-bit channel to 8 bits (32
    /// entries; the client passes its own table). With <paramref name="hueShades"/> (32 colours of hues.mul entry, the
    /// gump.def alias hue) every visible texel becomes <c>hueShades[red5]</c>, and stays visible only if that is not 0,
    /// as the client's GetColor16 path does. Every texel of <paramref name="dst"/> (w·h) is written. False on malformed data.
    /// </summary>
    public static bool DecodeRle(ReadOnlySpan<byte> src, int w, int h, Span<uint> dst, ReadOnlySpan<uint> expand, ReadOnlySpan<ushort> hueShades)
    {
        int words = src.Length >> 2;
        if (h <= 0 || w <= 0 || words < h || dst.Length < w * h || expand.Length < 32) return false;
        bool hued = hueShades.Length >= 32;
        for (int y = 0; y < h; y++)
        {
            int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(y * 4));
            int end = y < h - 1 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice((y + 1) * 4)) : words;
            if (start < h || end < start || end > words) return false;
            var row = dst.Slice(y * w, w);
            int x = 0;
            for (int i = start; i < end; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 4));
                int run = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 4 + 2)), w - x);
                uint c = 0;
                if (v != 0)
                {
                    if (hued) v = hueShades[(v >> 10) & 0x1F];
                    if (v != 0) c = expand[(v >> 10) & 0x1F] | expand[(v >> 5) & 0x1F] << 8 | expand[v & 0x1F] << 16 | 0xFF000000;
                }
                row.Slice(x, run).Fill(c);
                x += run;
            }
            row.Slice(x).Clear();
        }
        return true;
    }
}
