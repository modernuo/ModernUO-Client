using System.Buffers.Binary;
using System.IO.Compression;

namespace Uo.Render;

/// <summary>Minimal PNG writer (RGBA8, filter 0, ZLibStream). No dependencies.</summary>
public static class Png
{
    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
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

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <param name="rgba">width*height*4 bytes, R first.</param>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var fs = File.Create(path);
        Write(fs, width, height, rgba);
    }

    /// <summary>
    /// Writes the PNG to <paramref name="stream"/> (an atomic-replace stream, say). Rows are compressed as they are read
    /// and the IDAT data goes out in 64 KB chunks, so a large image (the ~118 MB world map) needs no full-size copy.
    /// </summary>
    /// <param name="rgba">width*height*4 bytes, R first.</param>
    public static void Write(Stream stream, int width, int height, ReadOnlySpan<byte> rgba)
    {
        int stride = width * 4;
        if (width <= 0 || height <= 0 || rgba.Length < (long)stride * height)
            throw new ArgumentException($"need {(long)stride * height} RGBA bytes for {width}x{height}, got {rgba.Length}");

        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(stream, "IHDR"u8, ihdr);

        using (var idat = new IdatStream(stream))
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                ReadOnlySpan<byte> filterNone = [0];
                for (int y = 0; y < height; y++)
                {
                    z.Write(filterNone);
                    z.Write(rgba.Slice(y * stride, stride));
                }
            }
            idat.FlushChunk();
        }

        Chunk(stream, "IEND"u8, []);
    }

    /// <summary>Buffers the zlib stream and writes it as IDAT chunks of up to 64 KB.</summary>
    sealed class IdatStream(Stream output) : Stream
    {
        readonly byte[] _buffer = new byte[64 * 1024];
        int _count;

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, _buffer.Length - _count);
                data[..n].CopyTo(_buffer.AsSpan(_count));
                _count += n;
                data = data[n..];
                if (_count == _buffer.Length) FlushChunk();
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void WriteByte(byte value) => Write([value]);

        public void FlushChunk()
        {
            if (_count == 0) return;
            Chunk(output, "IDAT"u8, _buffer.AsSpan(0, _count));
            _count = 0;
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        s.Write(type);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(b4, crc);
        s.Write(b4);
    }
}
