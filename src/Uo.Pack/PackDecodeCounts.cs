namespace Uo.Pack;

/// <summary>
/// Process-wide counts of compressed pack units decoded, per reader kind, logged by the client at exit ("[pack] decodes").
/// They let two runs of the same plan show they did the same decode work (codec comparisons, pop-in and profile jobs).
/// One interlocked add per decoded unit (a chunk is about 1 MiB), so they stay on in every build.
/// </summary>
public static class PackDecodeCounts
{
    private static long _pageChunks, _pageChunkBytes;
    private static long _chunkedBodies, _chunkedBodyBytes;
    private static long _chunkedSpans, _chunkedSpanBytes;
    private static long _records, _recordBytes;

    /// <summary>A page chunk decoded by the client's page cache (PackPages: art, tex, anim pages).</summary>
    public static void PageChunk(long rawBytes) => Add(ref _pageChunks, ref _pageChunkBytes, rawBytes);

    /// <summary>A chunked body decoded from a file by a <see cref="ChunkedDecoder"/> (GPU page residency, page headers).</summary>
    public static void ChunkedBody(long rawBytes) => Add(ref _chunkedBodies, ref _chunkedBodyBytes, rawBytes);

    /// <summary>A chunked body decoded from memory (<see cref="ChunkedFile.Decode"/>: regions and other whole files).</summary>
    public static void ChunkedSpan(long rawBytes) => Add(ref _chunkedSpans, ref _chunkedSpanBytes, rawBytes);

    /// <summary>A compressed record of a record store (gumps, text, multimap and other stores).</summary>
    public static void Record(long rawBytes) => Add(ref _records, ref _recordBytes, rawBytes);

    private static void Add(ref long count, ref long bytes, long n)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(ref bytes, n);
    }

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Summary() =>
        $"page chunks {Volatile.Read(ref _pageChunks)} ({Mib(Volatile.Read(ref _pageChunkBytes))} MiB), " +
        $"chunked file bodies {Volatile.Read(ref _chunkedBodies)} ({Mib(Volatile.Read(ref _chunkedBodyBytes))} MiB), " +
        $"chunked spans {Volatile.Read(ref _chunkedSpans)} ({Mib(Volatile.Read(ref _chunkedSpanBytes))} MiB), " +
        $"records {Volatile.Read(ref _records)} ({Mib(Volatile.Read(ref _recordBytes))} MiB)";
}
