using System.Buffers;
using System.IO.Compression;

namespace Uo.Pack;

/// <summary>
/// Decodes one compressed unit (a chunk of a chunked file, or a record) into a destination of exactly its raw size.
/// Instances are stateful (a codec context reused across calls) and not thread-safe: one per thread or worker
/// (<see cref="PackCodec.Create"/>, <see cref="PackCodec.ForThread"/>).
/// </summary>
public interface IPackDecoder : IDisposable
{
    ushort Codec { get; }

    /// <summary>Decodes all of <paramref name="src"/> into all of <paramref name="dst"/>; throws InvalidDataException
    /// unless the unit decodes to exactly dst.Length bytes and consumes all of src.</summary>
    void Decode(ReadOnlySpan<byte> src, Span<byte> dst);
}

/// <summary>
/// The pack's codecs. The codec is pack data: a record store names it per record (<see cref="StoreRecord.Codec"/>) and a
/// file names it through its <see cref="FileHeader.Kind"/> (<see cref="OfFileKind"/>). Every compressed byte a reader
/// decodes goes through an <see cref="IPackDecoder"/> from here, resolved once per file or record; readers do not branch
/// on codecs. Writers encode through <see cref="Encode(ushort, ReadOnlySpan{byte}, int)"/>.
/// </summary>
public static class PackCodec
{
    /// <summary>The payload as is.</summary>
    public const ushort Raw = 0;

    /// <summary>Brotli (pack revisions up to 1.5). Retired in revision 1.6: readers reject it with <see cref="Retired"/>.</summary>
    public const ushort RetiredBrotli = 1;

    /// <summary>One zstd frame (level 19, no dictionary, content size in the frame header). Revision 1.6.</summary>
    public const ushort Zstd = 2;

    /// <summary>The ship profile's zstd level.</summary>
    public const int ZstdLevel = 19;

    /// <summary>The level writers encode zstd with when they pass none: the ship profile's, or the converter's
    /// --zstd-level (for size and time comparisons).</summary>
    public static int WriteLevel = ZstdLevel;

    /// <summary>The encoder's window (2^22 = 4 MiB). zstd shrinks it to the unit's size, so a 1 MiB chunk uses 1 MiB.</summary>
    public const int ZstdWindowLog = 22;

    /// <summary>The largest window a reader accepts (2^23), so a forged frame cannot ask for a large decode buffer.</summary>
    public const int ZstdMaxWindowLog = 23;

    /// <summary>The error for a pack file or record in a codec this client no longer reads ("convert again").</summary>
    public static InvalidDataException Retired(string source) =>
        new($"'{source}' is Brotli-compressed: it comes from a pack revision older than 1.6, which this client no longer reads. Convert the client again with Uo.Converter.");

    /// <summary>The codec of a file's <see cref="FileHeader.Kind"/>; Brotli kinds are rejected with <see cref="Retired"/>.</summary>
    public static ushort OfFileKind(ushort kind, string source) => kind switch
    {
        FileKind.Raw => Raw,
        FileKind.ChunkedZstd => Zstd,
        FileKind.RetiredBrotli or FileKind.RetiredChunkedBrotli => throw Retired(source),
        _ => throw new InvalidDataException($"'{source}': unknown file kind {kind}"),
    };

    /// <summary>Throws unless this client decodes <paramref name="codec"/>.</summary>
    public static void Check(ushort codec, string source)
    {
        if (codec == Raw || codec == Zstd) return;
        if (codec == RetiredBrotli) throw Retired(source);
        throw new InvalidDataException($"'{source}': unknown codec {codec}");
    }

    /// <summary>The largest stored size a unit of <paramref name="rawBytes"/> can have in <paramref name="codec"/> (a
    /// record whose stored size is above it is invalid).</summary>
    public static long MaxStoredBytes(ushort codec, long rawBytes) => codec switch
    {
        Raw => rawBytes,
        Zstd => ZstandardEncoder.GetMaxCompressedLength(rawBytes),
        _ => -1,
    };

    /// <summary>A new decoder for <paramref name="codec"/>, owned by the caller (one per worker).</summary>
    public static IPackDecoder Create(ushort codec, string source = "pack")
    {
        Check(codec, source);
        return codec == Zstd ? new ZstdPackDecoder() : RawPackDecoder.Instance;
    }

    [ThreadStatic] private static ZstdPackDecoder? t_zstd;

    /// <summary>
    /// This thread's decoder for <paramref name="codec"/>: created on the thread's first use and kept for the thread's
    /// life, so a decode allocates nothing. For readers that decode from any thread (record stores, page chunks).
    /// </summary>
    public static IPackDecoder ForThread(ushort codec, string source = "pack")
    {
        if (codec == Zstd) return t_zstd ??= new ZstdPackDecoder();
        Check(codec, source);
        return RawPackDecoder.Instance;
    }

    /// <summary>Encodes <paramref name="raw"/> into <paramref name="dst"/> (at least <see cref="MaxStoredBytes"/> long) and
    /// returns the stored length.</summary>
    public static int Encode(ushort codec, ReadOnlySpan<byte> raw, Span<byte> dst, int? level = null)
    {
        switch (codec)
        {
            case Raw:
                raw.CopyTo(dst);
                return raw.Length;
            case Zstd:
                if (!ZstandardEncoder.TryCompress(raw, dst, out int n, level ?? WriteLevel, ZstdWindowLog))
                    throw new InvalidOperationException("zstd compress failed");
                return n;
            default:
                throw new ArgumentOutOfRangeException(nameof(codec), $"no encoder for codec {codec}");
        }
    }

    /// <summary>Encodes <paramref name="raw"/> into a new array of its stored length.</summary>
    public static byte[] Encode(ushort codec, ReadOnlySpan<byte> raw, int? level = null)
    {
        if (codec == Raw) return raw.ToArray();
        var buf = ArrayPool<byte>.Shared.Rent((int)MaxStoredBytes(codec, raw.Length));
        try
        {
            int n = Encode(codec, raw, buf, level);
            return buf.AsSpan(0, n).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private sealed class RawPackDecoder : IPackDecoder
    {
        public static readonly RawPackDecoder Instance = new();
        public ushort Codec => Raw;

        public void Decode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            if (src.Length != dst.Length) throw new InvalidDataException($"raw unit of {src.Length} bytes, expected {dst.Length}");
            src.CopyTo(dst);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// A zstd decompression context reused for every unit. The destination always holds the whole frame and the frame
    /// header states its content size, so zstd decodes in one pass straight into it: no window buffer, about 80 KiB of
    /// native context per decoder, no managed allocation per decode.
    /// </summary>
    private sealed class ZstdPackDecoder : IPackDecoder
    {
        private readonly ZstandardDecoder _d = new(ZstdMaxWindowLog);
        public ushort Codec => Zstd;

        public void Decode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            if (dst.IsEmpty)
            {
                // zstd's streaming call refuses an empty destination; an empty frame decodes to 0 bytes one-shot
                if (!ZstandardDecoder.TryDecompress(src, dst, out int n) || n != 0) throw new InvalidDataException("corrupt zstd unit");
                return;
            }
            _d.Reset();
            OperationStatus st;
            int consumed, written;
            try { st = _d.Decompress(src, dst, out consumed, out written); }
            catch (IOException e) { throw new InvalidDataException("corrupt zstd unit", e); }
            if (st != OperationStatus.Done || written != dst.Length || consumed != src.Length)
                throw new InvalidDataException($"corrupt zstd unit ({st}, {written} of {dst.Length} bytes, {consumed} of {src.Length} read)");
        }

        public void Dispose() => _d.Dispose();
    }
}
