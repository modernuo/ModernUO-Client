using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Uo.Converter;

/// <summary>Minimal RGBA8 PNG writer (filter "Up" on every row, zlib via ZLibStream). Debug output only.</summary>
public static class Png
{
    private static readonly uint[] Crc = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Writes pixels as RGBA8 (R in the low byte of each uint).</summary>
    public static void Write(string path, int w, int h, ReadOnlySpan<uint> rgba)
    {
        var bytes = MemoryMarshal.AsBytes(rgba);
        int stride = w * 4;
        var raw = new byte[(stride + 1) * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * (stride + 1);
            var row = bytes.Slice(y * stride, stride);
            if (y == 0) { raw[o] = 0; row.CopyTo(raw.AsSpan(o + 1)); continue; }
            raw[o] = 2; // Up
            var prev = bytes.Slice((y - 1) * stride, stride);
            for (int i = 0; i < stride; i++) raw[o + 1 + i] = (byte)(row[i] - prev[i]);
        }

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        using var fs = Uo.Pack.ReplacingFile.Create(path); // checked against the published packs roots like every output
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
        Chunk(fs, "IEND", []);
        fs.Commit();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(hdr, data.Length);
        for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
        s.Write(hdr);
        s.Write(data);
        uint crc = UpdateCrc(0xFFFFFFFF, hdr[4..]);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    /// <summary>Composites RGBA over a checkerboard so transparency is visible in viewers that flatten alpha.</summary>
    public static uint[] OverChecker(ReadOnlySpan<uint> px, int w, int h, int cell = 8)
    {
        var o = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = px[y * w + x];
                uint bg = (((x / cell) + (y / cell)) & 1) == 0 ? 0x60u : 0x90u;
                uint a = p >> 24;
                uint r = ((p & 0xFF) * a + bg * (255 - a)) / 255;
                uint g = (((p >> 8) & 0xFF) * a + bg * (255 - a)) / 255;
                uint b = (((p >> 16) & 0xFF) * a + bg * (255 - a)) / 255;
                o[y * w + x] = r | (g << 8) | (b << 16) | 0xFF000000;
            }
        return o;
    }
}
