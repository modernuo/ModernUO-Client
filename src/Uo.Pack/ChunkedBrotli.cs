using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Uo.Pack;

/// <summary>Kind=2 body header, right after the FileHeader; followed by u32 CompressedSize[ChunkCount], then the chunks.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ChunkedHeader
{
    public uint ChunkBytes;
    public ulong LogicalBytes;
    public uint ChunkCount;

    public readonly int ChunkLength(int i) => (int)Math.Min(ChunkBytes, LogicalBytes - (ulong)i * ChunkBytes);
}

/// <summary>
/// Chunked Brotli (FileHeader.Kind = 2): the logical payload (every byte after the FileHeader) split into fixed-size
/// chunks that compress independently, so several workers can decode one file. See "Distribution and chunked
/// compression" in docs/modern-client/pack-format-v0.md.
/// </summary>
public static class ChunkedBrotli
{
    public const int DefaultChunkBytes = 1 << 20;
    public const int Quality = 5;
    public const int Window = 22;
    public static readonly int HeaderBytes = StructIo.SizeOf<ChunkedHeader>();

    /// <summary>Writes the Kind=2 body (header, size table, chunks) of <paramref name="logical"/>.</summary>
    public static void Write(Stream s, ReadOnlySpan<byte> logical, int chunkBytes = DefaultChunkBytes, int quality = Quality)
    {
        if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        int count = (int)((logical.Length + (long)chunkBytes - 1) / chunkBytes);
        var h = new ChunkedHeader { ChunkBytes = (uint)chunkBytes, LogicalBytes = (ulong)logical.Length, ChunkCount = (uint)count };
        var sizes = new uint[count];
        var buf = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(chunkBytes));
        using var data = new MemoryStream();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var src = logical.Slice(i * chunkBytes, h.ChunkLength(i));
                if (!BrotliEncoder.TryCompress(src, buf, out int n, quality, Window)) throw new InvalidOperationException("brotli compress failed");
                sizes[i] = (uint)n;
                data.Write(buf, 0, n);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        StructIo.Write(s, in h);
        StructIo.WriteSpan<uint>(s, sizes);
        s.Write(data.GetBuffer(), 0, (int)data.Length);
    }

    /// <summary>
    /// Single-threaded decode of a Kind=2 body held in memory. <paramref name="dest"/>[0] receives logical byte
    /// <paramref name="skip"/>; logical bytes before it go to <paramref name="prefix"/> (as far as it reaches).
    /// </summary>
    public static ChunkedHeader Decode(ReadOnlySpan<byte> body, Span<byte> dest, long skip = 0, Span<byte> prefix = default)
    {
        var h = MemoryMarshal.Read<ChunkedHeader>(body);
        var sizes = MemoryMarshal.Cast<byte, uint>(body.Slice(HeaderBytes, (int)h.ChunkCount * 4));
        if ((long)h.LogicalBytes - skip > dest.Length) throw new ArgumentException("destination too small");
        int pos = HeaderBytes + (int)h.ChunkCount * 4;
        byte[]? scratch = null;
        try
        {
            for (int i = 0; i < h.ChunkCount; i++)
            {
                var src = body.Slice(pos, (int)sizes[i]);
                pos += (int)sizes[i];
                long a = (long)i * h.ChunkBytes;
                int len = h.ChunkLength(i);
                if (a >= skip) { DecodeExact(src, dest.Slice((int)(a - skip), len)); continue; }
                scratch ??= ArrayPool<byte>.Shared.Rent((int)h.ChunkBytes);
                DecodeExact(src, scratch.AsSpan(0, len));
                Split(scratch.AsSpan(0, len), a, skip, dest, prefix);
            }
        }
        finally { if (scratch != null) ArrayPool<byte>.Shared.Return(scratch); }
        return h;
    }

    internal static void DecodeExact(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (!BrotliDecoder.TryDecompress(src, dst, out int n) || n != dst.Length)
            throw new InvalidDataException("corrupt Brotli chunk");
    }

    /// <summary>Copies a decoded chunk starting at logical offset <paramref name="a"/> into prefix [0, skip) and dest [skip, ...).</summary>
    internal static void Split(ReadOnlySpan<byte> chunk, long a, long skip, Span<byte> dest, Span<byte> prefix)
    {
        long b = a + chunk.Length;
        long pe = Math.Min(Math.Min(b, skip), prefix.Length);
        if (pe > a) chunk[..(int)(pe - a)].CopyTo(prefix[(int)a..]);
        if (b > skip)
        {
            long from = Math.Max(a, skip);
            chunk[(int)(from - a)..].CopyTo(dest[(int)(from - skip)..]);
        }
    }
}

/// <summary>Forward-only stream over a Kind=2 body (sequential, one chunk buffered). Used by AtlasPage.OpenChain.</summary>
public sealed class ChunkedBrotliStream : Stream
{
    private readonly Stream _src;
    private readonly ChunkedHeader _h;
    private readonly uint[] _sizes;
    private readonly byte[] _chunk, _comp;
    private int _next, _len, _pos;

    public ChunkedBrotliStream(Stream src)
    {
        _src = src;
        _h = StructIo.Read<ChunkedHeader>(src);
        _sizes = new uint[_h.ChunkCount];
        StructIo.ReadSpan<uint>(src, _sizes);
        _chunk = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, _h.ChunkBytes));
        _comp = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength((int)Math.Max(1, _h.ChunkBytes)));
    }

    public ChunkedHeader Header => _h;

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0)
        {
            if (_pos == _len)
            {
                if (_next >= _h.ChunkCount) break;
                var c = _comp.AsSpan(0, (int)_sizes[_next]);
                _src.ReadExactly(c);
                _len = _h.ChunkLength(_next);
                ChunkedBrotli.DecodeExact(c, _chunk.AsSpan(0, _len));
                _next++;
                _pos = 0;
            }
            int n = Math.Min(buffer.Length, _len - _pos);
            _chunk.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n; total += n;
            buffer = buffer[n..];
        }
        return total;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _src.Dispose();
            ArrayPool<byte>.Shared.Return(_chunk);
            ArrayPool<byte>.Shared.Return(_comp);
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Parallel Kind=2 decoder: <c>workers - 1</c> persistent threads plus the calling thread pull chunks from a shared
/// counter, read their compressed bytes with RandomAccess and decode them straight into the caller's span at their
/// logical offsets. Per-worker buffers are reused, so a steady-state Decode does not allocate. One Decode at a time.
/// </summary>
public sealed unsafe class ChunkedBrotliDecoder : IDisposable
{
    private readonly Thread[] _threads;
    // Kernel events rather than SemaphoreSlim/CountdownEvent: their blocking paths allocate under contention.
    private readonly AutoResetEvent[] _go;
    private readonly AutoResetEvent _done = new(false);
    private int _pending;
    private readonly byte[][] _comp, _scratch;
    private volatile bool _stop;

    private SafeFileHandle? _fh;
    private ChunkedHeader _h;
    private uint[] _sizes = new uint[64];
    private long[] _offs = new long[64];
    private byte* _dest, _prefix;
    private long _destLen, _prefixLen, _skip;
    private int _next;
    private Exception? _error;

    public int Workers => _threads.Length + 1;

    public ChunkedBrotliDecoder(int workers)
    {
        workers = Math.Max(1, workers);
        _comp = new byte[workers][];
        _scratch = new byte[workers][];
        for (int i = 0; i < workers; i++) { _comp[i] = []; _scratch[i] = []; }
        _threads = new Thread[workers - 1];
        _go = new AutoResetEvent[workers - 1];
        for (int i = 0; i < _go.Length; i++) _go[i] = new AutoResetEvent(false);
        for (int i = 0; i < _threads.Length; i++)
        {
            int id = i + 1;
            _threads[i] = new Thread(() => Loop(id)) { IsBackground = true, Name = $"chunk-decode-{id}" };
            _threads[i].Start();
        }
    }

    private void Loop(int id)
    {
        while (true)
        {
            _go[id - 1].WaitOne();
            if (_stop) return;
            Run(id);
            if (Interlocked.Decrement(ref _pending) == 0) _done.Set();
        }
    }

    /// <summary>
    /// Decodes the Kind=2 body that starts at <paramref name="bodyOffset"/> in <paramref name="fh"/>. dest[0] receives
    /// logical byte <paramref name="skip"/>; bytes before it go to <paramref name="prefix"/> (e.g. a page's PageHeader).
    /// </summary>
    public ChunkedHeader Decode(SafeFileHandle fh, long bodyOffset, Span<byte> dest, long skip = 0, Span<byte> prefix = default)
    {
        ChunkedHeader h = default;
        RandomAccess.Read(fh, MemoryMarshal.AsBytes(new Span<ChunkedHeader>(ref h)), bodyOffset);
        return Decode(fh, bodyOffset, h, dest, skip, prefix);
    }

    /// <summary>As <see cref="Decode(SafeFileHandle, long, Span{byte}, long, Span{byte})"/> with the body's ChunkedHeader already read by the caller.</summary>
    public ChunkedHeader Decode(SafeFileHandle fh, long bodyOffset, in ChunkedHeader header, Span<byte> dest, long skip = 0, Span<byte> prefix = default)
    {
        var h = header;
        if ((long)h.LogicalBytes - skip > dest.Length) throw new ArgumentException("destination too small");
        int count = (int)h.ChunkCount;
        if (_sizes.Length < count) { _sizes = new uint[count]; _offs = new long[count]; }
        long tableAt = bodyOffset + ChunkedBrotli.HeaderBytes;
        RandomAccess.Read(fh, MemoryMarshal.AsBytes(_sizes.AsSpan(0, count)), tableAt);
        int maxComp = BrotliEncoder.GetMaxCompressedLength((int)h.ChunkBytes);
        for (int i = 0; i < _comp.Length; i++)
        {
            if (_comp[i].Length < maxComp) _comp[i] = new byte[maxComp];
            if (_scratch[i].Length < h.ChunkBytes) _scratch[i] = new byte[h.ChunkBytes];
        }
        long off = tableAt + 4L * count;
        for (int i = 0; i < count; i++) { _offs[i] = off; off += _sizes[i]; }

        fixed (byte* d = dest)
        fixed (byte* p = prefix)
        {
            _fh = fh; _h = h; _dest = d; _destLen = dest.Length; _prefix = p; _prefixLen = prefix.Length; _skip = skip;
            _next = 0; _error = null;
            int helpers = Math.Min(_threads.Length, Math.Max(0, count - 1));
            _pending = helpers;
            for (int i = 0; i < helpers; i++) _go[i].Set();
            Run(0);
            if (helpers > 0) _done.WaitOne();
            _fh = null; _dest = null; _prefix = null;
        }
        if (_error != null) throw new InvalidDataException("chunk decode failed", _error);
        return h;
    }

    public ChunkedHeader Decode(string path, long bodyOffset, Span<byte> dest, long skip = 0, Span<byte> prefix = default)
    {
        using var fh = File.OpenHandle(path);
        return Decode(fh, bodyOffset, dest, skip, prefix);
    }

    private void Run(int id)
    {
        try
        {
            int i;
            while ((i = Interlocked.Increment(ref _next) - 1) < _h.ChunkCount)
            {
                int cs = (int)_sizes[i];
                if (_comp[id].Length < cs) throw new InvalidDataException("chunk larger than its bound");
                var src = _comp[id].AsSpan(0, cs);
                if (RandomAccess.Read(_fh!, src, _offs[i]) != cs) throw new EndOfStreamException();
                long a = (long)i * _h.ChunkBytes;
                int len = _h.ChunkLength(i);
                var dest = new Span<byte>(_dest, (int)_destLen);
                if (a >= _skip) { ChunkedBrotli.DecodeExact(src, dest.Slice((int)(a - _skip), len)); continue; }
                var tmp = _scratch[id].AsSpan(0, len);
                ChunkedBrotli.DecodeExact(src, tmp);
                ChunkedBrotli.Split(tmp, a, _skip, dest, new Span<byte>(_prefix, (int)_prefixLen));
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            Volatile.Write(ref _next, int.MaxValue / 2);
        }
    }

    public void Dispose()
    {
        _stop = true;
        foreach (var e in _go) e.Set();
        foreach (var t in _threads) t.Join();
        foreach (var e in _go) e.Dispose();
        _done.Dispose();
    }
}
