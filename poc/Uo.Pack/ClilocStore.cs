using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Uo.Pack;

// text/cliloc.bin (docs/modern-client/pack-format.md, "text/cliloc.bin"): a record store with one record per language
// and at most one record of the shard's own strings (Clilocs.txt folded in by the converter). Every record holds a
// ClilocTable payload: sorted ids, string ends, and the UTF-8 strings either as one blob (flat) or in Brotli blocks of
// about 64 KiB (blocked, --cliloc-blocks), decoded on the first lookup inside them.

/// <summary>
/// One cliloc table, the raw payload of a cliloc record. Flat layout:
/// <code>
///   count u32, blobBytes u32
///   ids  i32[count]    strictly ascending (binary search)
///   ends u32[count]    string i is blob[ends[i−1] (0 for i = 0) .. ends[i])
///   blob               UTF-8, strings back to back in id order
/// </code>
/// Blocked layout (record meta2 bit <see cref="ClilocStore.LayoutBlocked"/>): the same ids and ends, but the blob is cut
/// at string boundaries into blocks, each a Brotli stream decoded on its first lookup:
/// <code>
///   count u32, blobBytes u32, blockCount u32, reserved u32 (0)
///   ids          i32[count]
///   ends         u32[count]
///   blockStarts  u32[blockCount + 1]   blob offset of each block, [0] = 0, strictly ascending, last = blobBytes
///   blockOffsets u32[blockCount + 1]   offset of each compressed block after the tables, [0] = 0, last = their size
///   blocks       Brotli streams, back to back
/// </code>
/// Strings are created on first use and cached; <see cref="TryGetString"/> is thread-safe, never throws, and returns the
/// same instance for an id every time.
/// </summary>
public sealed class ClilocTable
{
    public const int HeaderBytes = 8;
    public const int BlockedHeaderBytes = 16;
    /// <summary>Largest raw payload a reader accepts (the largest client file, Cliloc.jpn, is about 9 MiB raw).</summary>
    public const int MaxRawBytes = 256 << 20;
    /// <summary>Target block size of the blocked layout (a block ends at the first string end at or after it).</summary>
    public const int DefaultBlockBytes = 64 << 10;
    /// <summary>Largest decoded block a reader accepts (a cliloc string is at most 32,767 UTF-16 units, 96 KiB of UTF-8).</summary>
    public const int MaxBlockBytes = 1 << 20;

    private readonly byte[] _data;
    private readonly int _count;
    private readonly int _idsAt, _endsAt;
    private readonly int _blobAt; // flat: the blob; blocked: the first compressed block
    private readonly string?[] _cache;
    // blocked layout only
    private readonly int _blockCount, _blockStartsAt, _blockOffsetsAt;
    private readonly byte[]?[]? _blocks;
    private readonly object? _blockLock;
    private int _broken;

    private ClilocTable(byte[] data, int count, bool blocked, int blockCount)
    {
        _data = data;
        _count = count;
        int at = blocked ? BlockedHeaderBytes : HeaderBytes;
        _idsAt = at;
        _endsAt = at + count * 4;
        _cache = new string?[count];
        if (!blocked)
        {
            _blobAt = at + count * 8;
            return;
        }
        _blockCount = blockCount;
        _blockStartsAt = at + count * 8;
        _blockOffsetsAt = _blockStartsAt + (blockCount + 1) * 4;
        _blobAt = _blockOffsetsAt + (blockCount + 1) * 4;
        _blocks = new byte[]?[blockCount];
        _blockLock = new object();
    }

    public int Count => _count;

    public bool Blocked => _blocks != null;

    /// <summary>Bytes held: the payload (index and UTF-8 text, compressed for the blocked layout) plus the blocks decoded so far; strings made so far are extra.</summary>
    public long PayloadBytes
    {
        get
        {
            long n = _data.Length;
            if (_blocks != null)
                for (int i = 0; i < _blocks.Length; i++) n += Volatile.Read(ref _blocks[i])?.Length ?? 0;
            return n;
        }
    }

    /// <summary>Blocks of the blocked layout, and how many have been decoded (tests and measurements).</summary>
    public int BlockCount => _blockCount;
    public int BlocksLoaded
    {
        get
        {
            int n = 0;
            if (_blocks != null)
                for (int i = 0; i < _blocks.Length; i++) if (Volatile.Read(ref _blocks[i]) != null) n++;
            return n;
        }
    }

    /// <summary>Measurements: blocks decoded by every table, and the Stopwatch ticks spent decoding them.</summary>
    public static long StatBlockLoads, StatBlockTicks;

    private ReadOnlySpan<int> Ids => MemoryMarshal.Cast<byte, int>(_data.AsSpan(_idsAt, _count * 4));
    private ReadOnlySpan<uint> Ends => MemoryMarshal.Cast<byte, uint>(_data.AsSpan(_endsAt, _count * 4));
    private ReadOnlySpan<uint> BlockStarts => MemoryMarshal.Cast<byte, uint>(_data.AsSpan(_blockStartsAt, (_blockCount + 1) * 4));
    private ReadOnlySpan<uint> BlockOffsets => MemoryMarshal.Cast<byte, uint>(_data.AsSpan(_blockOffsetsAt, (_blockCount + 1) * 4));

    public int IdAt(int index) => Ids[index];

    /// <summary>Index of <paramref name="id"/>, or −1.</summary>
    public int IndexOf(int id)
    {
        int i = Ids.BinarySearch(id);
        return i >= 0 ? i : -1;
    }

    /// <summary>The UTF-8 bytes of string <paramref name="index"/>; false when its block can't be decoded (blocked layout).</summary>
    public bool TryUtf8At(int index, out ReadOnlySpan<byte> utf8)
    {
        var ends = Ends;
        uint start = index == 0 ? 0 : ends[index - 1];
        int length = (int)(ends[index] - start);
        if (_blocks == null)
        {
            utf8 = _data.AsSpan(_blobAt + (int)start, length);
            return true;
        }
        if (length == 0)
        {
            utf8 = default;
            return true;
        }
        // the block holding the string: the last block that starts at or before it (validated: it ends inside it)
        var starts = BlockStarts.Slice(0, _blockCount);
        int k = starts.BinarySearch(start);
        if (k < 0) k = ~k - 1;
        var block = Block(k);
        if (block == null)
        {
            utf8 = default;
            return false;
        }
        utf8 = block.AsSpan((int)(start - starts[k]), length);
        return true;
    }

    public ReadOnlySpan<byte> Utf8At(int index) => TryUtf8At(index, out var s) ? s : throw new InvalidDataException("cliloc block could not be decoded");

    private byte[]? Block(int k)
    {
        var b = Volatile.Read(ref _blocks![k]);
        if (b != null) return b;
        if (Volatile.Read(ref _broken) != 0) return null;
        lock (_blockLock!)
        {
            // a thread that waited here finds the block the first one decoded
            b = Volatile.Read(ref _blocks[k]);
            if (b != null) return b;
            long t0 = Stopwatch.GetTimestamp();
            var starts = BlockStarts;
            var offsets = BlockOffsets;
            var raw = new byte[starts[k + 1] - starts[k]];
            var src = _data.AsSpan(_blobAt + (int)offsets[k], (int)(offsets[k + 1] - offsets[k]));
            if (!BrotliDecoder.TryDecompress(src, raw, out int written) || written != raw.Length)
            {
                // a corrupt block: its strings are absent (lookups fall through to the next table), never an exception
                Volatile.Write(ref _broken, 1);
                return null;
            }
            Interlocked.Increment(ref StatBlockLoads);
            Interlocked.Add(ref StatBlockTicks, Stopwatch.GetTimestamp() - t0);
            Volatile.Write(ref _blocks[k], raw);
            return raw;
        }
    }

    /// <summary>True once a block failed to decode (its strings are absent).</summary>
    public bool Broken => Volatile.Read(ref _broken) != 0;

    /// <summary>The string at <paramref name="index"/>, created on first use (null when its block can't be decoded). Thread-safe.</summary>
    public string? StringAt(int index)
    {
        var s = Volatile.Read(ref _cache[index]);
        if (s != null) return s;
        if (!TryUtf8At(index, out var utf8)) return null;
        s = Encoding.UTF8.GetString(utf8);
        return Interlocked.CompareExchange(ref _cache[index], s, null) ?? s;
    }

    public bool TryGetString(int id, out string text)
    {
        int i = IndexOf(id);
        string? s = i < 0 ? null : StringAt(i);
        text = s!;
        return s != null;
    }

    /// <summary>Strings created so far (tests and measurements).</summary>
    public int Materialized
    {
        get
        {
            int n = 0;
            foreach (var s in _cache) if (s != null) n++;
            return n;
        }
    }

    /// <summary>
    /// Parses a flat payload (taking ownership of <paramref name="data"/>, which must be exactly the payload). Null when
    /// it is malformed: sizes that don't add up, ids not strictly ascending, string ends decreasing or past the blob.
    /// </summary>
    public static ClilocTable? TryParse(byte[] data)
    {
        if (data.Length < HeaderBytes || data.Length > MaxRawBytes) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint blob = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (count > (uint)(data.Length - HeaderBytes) / 8 || (ulong)HeaderBytes + count * 8UL + blob != (ulong)data.Length) return null;
        var t = new ClilocTable(data, (int)count, false, 0);
        return CheckIndex(t.Ids, t.Ends, blob) ? t : null;
    }

    private static bool CheckIndex(ReadOnlySpan<int> ids, ReadOnlySpan<uint> ends, uint blob)
    {
        uint prevEnd = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0 && ids[i] <= ids[i - 1]) return false;
            if (ends[i] < prevEnd || ends[i] > blob) return false;
            prevEnd = ends[i];
        }
        return ids.Length > 0 ? prevEnd == blob : blob == 0;
    }

    /// <summary>
    /// Parses a blocked payload (taking ownership of <paramref name="data"/>). Null when malformed: besides the flat
    /// checks, block starts that aren't strictly ascending from 0 to blobBytes, a block larger than
    /// <see cref="MaxBlockBytes"/>, compressed offsets that decrease, pass the payload or exceed Brotli's bound, or a
    /// string that crosses a block boundary. Blocks are decoded later, on first use.
    /// </summary>
    public static ClilocTable? TryParseBlocked(byte[] data)
    {
        if (data.Length < BlockedHeaderBytes || data.Length > MaxRawBytes) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint blob = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) != 0) return null;
        ulong tables = (ulong)BlockedHeaderBytes + count * 8UL + (blocks + 1UL) * 8UL;
        if (tables > (ulong)data.Length) return null;
        if ((blob == 0) != (blocks == 0)) return null;
        var t = new ClilocTable(data, (int)count, true, (int)blocks);
        var ends = t.Ends;
        if (!CheckIndex(t.Ids, ends, blob)) return null;
        var starts = t.BlockStarts;
        var offsets = t.BlockOffsets;
        ulong compressed = (ulong)data.Length - tables;
        if (starts[0] != 0 || starts[(int)blocks] != blob || offsets[0] != 0 || offsets[(int)blocks] != compressed) return null;
        for (int k = 0; k < blocks; k++)
        {
            uint size = starts[k + 1] - starts[k];
            if (starts[k + 1] <= starts[k] || size > MaxBlockBytes) return null;
            if (offsets[k + 1] < offsets[k] || offsets[k + 1] - offsets[k] > (uint)BrotliEncoder.GetMaxCompressedLength((int)size)) return null;
        }
        // every non-empty string lies inside one block
        int b = 0;
        uint startOf = 0;
        for (int i = 0; i < ends.Length; i++)
        {
            uint end = ends[i];
            if (end > startOf)
            {
                while (starts[b + 1] <= startOf) b++;
                if (end > starts[b + 1]) return null;
            }
            startOf = end;
        }
        return t;
    }

    /// <summary>
    /// Builds a table from slices of <paramref name="source"/>: (id, start, length) with unique ids, in any order. The
    /// client's MUL path uses it to index a cliloc file without creating its strings.
    /// </summary>
    public static ClilocTable FromSlices(ReadOnlySpan<byte> source, (int Id, int Start, int Length)[] entries)
    {
        Array.Sort(entries, (a, b) => a.Id.CompareTo(b.Id));
        long blob = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (i > 0 && entries[i].Id == entries[i - 1].Id) throw new ArgumentException($"duplicate cliloc id {entries[i].Id}");
            blob += entries[i].Length;
        }
        var data = new byte[checked(HeaderBytes + entries.Length * 8 + (int)blob)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)blob);
        var ids = MemoryMarshal.Cast<byte, int>(data.AsSpan(HeaderBytes, entries.Length * 4));
        var ends = MemoryMarshal.Cast<byte, uint>(data.AsSpan(HeaderBytes + entries.Length * 4, entries.Length * 4));
        int at = HeaderBytes + entries.Length * 8;
        uint end = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            ids[i] = entries[i].Id;
            source.Slice(entries[i].Start, entries[i].Length).CopyTo(data.AsSpan(at + (int)end));
            end += (uint)entries[i].Length;
            ends[i] = end;
        }
        return new ClilocTable(data, entries.Length, false, 0);
    }

    /// <summary>Builds a flat payload from (id, UTF-8) entries with unique ids, in any order.</summary>
    public static byte[] Build(IReadOnlyList<(int Id, byte[] Utf8)> entries)
    {
        var sorted = entries.OrderBy(e => e.Id).ToArray();
        long blob = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > 0 && sorted[i].Id == sorted[i - 1].Id) throw new ArgumentException($"duplicate cliloc id {sorted[i].Id}");
            blob += sorted[i].Utf8.Length;
        }
        var data = new byte[checked(HeaderBytes + sorted.Length * 8 + (int)blob)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)sorted.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)blob);
        var ids = MemoryMarshal.Cast<byte, int>(data.AsSpan(HeaderBytes, sorted.Length * 4));
        var ends = MemoryMarshal.Cast<byte, uint>(data.AsSpan(HeaderBytes + sorted.Length * 4, sorted.Length * 4));
        int at = HeaderBytes + sorted.Length * 8;
        uint end = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            ids[i] = sorted[i].Id;
            sorted[i].Utf8.CopyTo(data.AsSpan(at + (int)end));
            end += (uint)sorted[i].Utf8.Length;
            ends[i] = end;
        }
        return data;
    }

    /// <summary>
    /// Re-encodes a flat payload in the blocked layout: blocks of about <paramref name="blockBytes"/> that end at a
    /// string end, each compressed with Brotli at <paramref name="quality"/> (window 22).
    /// </summary>
    public static byte[] ToBlocked(byte[] flat, int blockBytes = DefaultBlockBytes, int quality = 9)
    {
        var t = TryParse((byte[])flat.Clone()) ?? throw new ArgumentException("not a valid flat cliloc payload");
        uint count = (uint)t._count;
        uint blob = BinaryPrimitives.ReadUInt32LittleEndian(flat.AsSpan(4));
        var ends = t.Ends;
        var starts = new List<uint> { 0 };
        uint blockStart = 0;
        for (int i = 0; i < ends.Length; i++)
            if (ends[i] - blockStart >= blockBytes && ends[i] < blob)
            {
                starts.Add(ends[i]);
                blockStart = ends[i];
            }
        if (blob > 0) starts.Add(blob);
        else starts.Clear();
        int blocks = Math.Max(0, starts.Count - 1);
        if (blocks == 0) starts = [0];
        int srcAt = HeaderBytes + (int)count * 8;
        var compressed = new byte[blocks][];
        Parallel.For(0, blocks, k => compressed[k] = RecordStoreWriter.Encode(flat.AsSpan(srcAt + (int)starts[k], (int)(starts[k + 1] - starts[k])), StoreConst.CodecBrotli, quality));
        long total = compressed.Sum(c => (long)c.Length);
        var data = new byte[checked(BlockedHeaderBytes + (int)count * 8 + (blocks + 1) * 8 + (int)total)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), blob);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)blocks);
        flat.AsSpan(HeaderBytes, (int)count * 8).CopyTo(data.AsSpan(BlockedHeaderBytes));
        int at = BlockedHeaderBytes + (int)count * 8;
        for (int k = 0; k <= blocks; k++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + k * 4), starts[k]);
        at += (blocks + 1) * 4;
        uint off = 0;
        int blocksAt = at + (blocks + 1) * 4;
        for (int k = 0; k <= blocks; k++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + k * 4), off);
            if (k == blocks) break;
            compressed[k].CopyTo(data.AsSpan(blocksAt + (int)off));
            off += (uint)compressed[k].Length;
        }
        return data;
    }

    /// <summary>A language code (1..4 ASCII letters or digits, e.g. "enu") packed into record meta0/meta1.</summary>
    public static (ushort Meta0, ushort Meta1) PackLanguage(string code)
    {
        if (code.Length is < 1 or > 4 || !code.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException($"language code '{code}'");
        code = code.ToLowerInvariant();
        Span<byte> b = stackalloc byte[4];
        for (int i = 0; i < 4; i++) b[i] = i < code.Length ? (byte)code[i] : (byte)0;
        return ((ushort)(b[0] | b[1] << 8), (ushort)(b[2] | b[3] << 8));
    }

    /// <summary>The language code of a record's meta, or null when it is not 1..4 lowercase ASCII letters or digits.</summary>
    public static string? UnpackLanguage(ushort meta0, ushort meta1)
    {
        Span<char> c = stackalloc char[4];
        int n = 0;
        ReadOnlySpan<int> bytes = [meta0 & 0xFF, meta0 >> 8, meta1 & 0xFF, meta1 >> 8];
        for (int i = 0; i < 4; i++)
        {
            int b = bytes[i];
            if (b == 0)
            {
                for (int j = i + 1; j < 4; j++) if (bytes[j] != 0) return null;
                break;
            }
            if (!(b is >= 'a' and <= 'z' || b is >= '0' and <= '9')) return null;
            c[n++] = (char)b;
        }
        return n == 0 ? null : new string(c.Slice(0, n));
    }
}

/// <summary>The records of a text/cliloc.bin, resolved through its slots as the client resolves them.</summary>
public sealed class ClilocDirectory
{
    /// <summary>Language code → record index, in slot order.</summary>
    public required Dictionary<string, int> Languages { get; init; }
    /// <summary>The languages in slot order (enu first when the converter wrote it).</summary>
    public required List<string> Order { get; init; }
    /// <summary>The record of the shard's own strings (Clilocs.txt folded in), or −1.</summary>
    public int Overrides { get; init; } = -1;
}

/// <summary>Record meta of text/cliloc.bin and the slot rules the client and the converter's verification share.</summary>
public static class ClilocStore
{
    /// <summary>meta2 low byte: what the record holds.</summary>
    public const ushort KindLanguage = 0, KindOverrides = 1;
    /// <summary>meta2 bit 8: the payload has the blocked layout (<see cref="ClilocTable.TryParseBlocked"/>).</summary>
    public const ushort LayoutBlocked = 0x100;

    public static ushort Meta2(ushort kind, bool blocked) => (ushort)(kind | (blocked ? LayoutBlocked : 0));

    /// <summary>
    /// Walks the slots in order and resolves every record they reach. Throws <see cref="InvalidDataException"/> (the
    /// whole store is unusable) for an unknown meta2 kind or bit, a language record without a valid code, a language
    /// code reached twice, an overrides record with a code, or more than one overrides record. Records no slot reaches
    /// are ignored, as the client ignores them.
    /// </summary>
    public static ClilocDirectory Resolve(RecordStore store)
    {
        var langs = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        int overrides = -1;
        var seen = new HashSet<uint>();
        foreach (var slot in store.Slots)
        {
            if (slot.Record == 0) continue;
            if (!seen.Add(slot.Record)) throw new InvalidDataException($"cliloc: record {slot.Record - 1} is reached by two slots");
            int rec = (int)slot.Record - 1;
            ref readonly var r = ref store.Records[rec];
            if ((r.Meta2 & ~(0xFF | LayoutBlocked)) != 0) throw new InvalidDataException($"cliloc: record {rec} has unknown meta2 0x{r.Meta2:X4}");
            switch (r.Meta2 & 0xFF)
            {
                case KindLanguage:
                    var lang = ClilocTable.UnpackLanguage(r.Meta0, r.Meta1) ?? throw new InvalidDataException($"cliloc: record {rec} has no valid language code");
                    if (!langs.TryAdd(lang, rec)) throw new InvalidDataException($"cliloc: language '{lang}' appears twice");
                    order.Add(lang);
                    break;
                case KindOverrides:
                    if (r.Meta0 != 0 || r.Meta1 != 0) throw new InvalidDataException($"cliloc: overrides record {rec} has a language code");
                    if (overrides >= 0) throw new InvalidDataException("cliloc: two overrides records");
                    overrides = rec;
                    break;
                default:
                    throw new InvalidDataException($"cliloc: record {rec} has unknown kind {r.Meta2 & 0xFF}");
            }
        }
        return new ClilocDirectory { Languages = langs, Order = order, Overrides = overrides };
    }

    /// <summary>
    /// Reads and parses one record (any thread). Null, with the reason, when its size is impossible, its read or Brotli
    /// decode fails, or its payload is malformed; never throws for bad input.
    /// </summary>
    public static ClilocTable? ReadTable(RecordStore store, int record, out string? error)
    {
        ref readonly var r = ref store.Records[record];
        bool blocked = (r.Meta2 & LayoutBlocked) != 0;
        if (r.RawBytes < (blocked ? ClilocTable.BlockedHeaderBytes : ClilocTable.HeaderBytes) || r.RawBytes > ClilocTable.MaxRawBytes ||
            (r.Codec == StoreConst.CodecBrotli && r.StoredBytes > BrotliEncoder.GetMaxCompressedLength((int)r.RawBytes)))
        {
            error = $"an impossible size ({r.RawBytes} bytes)";
            return null;
        }
        try
        {
            var data = new byte[r.RawBytes];
            store.Read(r, data);
            var table = blocked ? ClilocTable.TryParseBlocked(data) : ClilocTable.TryParse(data);
            error = table == null ? "a malformed payload" : null;
            return table;
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException)
        {
            error = $"a failed read ({e.Message})";
            return null;
        }
    }
}

/// <summary>
/// The shard's own strings, Clilocs.txt: one "number text" per line (a tab or the first run of spaces separates them;
/// blank lines and lines starting with # are skipped; a later line wins). The client reads a loose file with it, and
/// the converter folds the file into text/cliloc.bin with it, so both read it the same way.
/// </summary>
public static class ClilocOverrides
{
    public const string FileName = "Clilocs.txt";

    public static Dictionary<int, string> Parse(IEnumerable<string> lines)
    {
        var ours = new Dictionary<int, string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            // the text itself may contain anything, tabs included, after the separator
            int at = trimmed.IndexOfAny(['\t', ' ']);
            if (at <= 0 || !int.TryParse(trimmed.AsSpan(0, at), out int number)) continue;
            ours[number] = trimmed[(at + 1)..].TrimStart();
        }
        return ours;
    }
}

/// <summary>
/// The Cliloc.* language files of a client folder (the converter's input). The name is matched without regard to case
/// (Cliloc.enu, CLILOC.DEU, cliloc.fra); the extension is the language code (1..4 ASCII letters or digits). Exports and
/// backups next to them (a CliLoc.csv) are told apart by content, not by name (bak is a language code too): a cliloc file
/// is BWT-compressed (byte 3 = 0x8E, as the client tests) or a raw table whose records add up exactly to the file's end.
/// An explicit skip list drops more by extension.
/// </summary>
public static class ClilocDiscovery
{
    /// <summary>(language code, path), enu first. Throws when there is no language left, no enu, or two files for one code.</summary>
    public static List<(string Lang, string Path)> Find(string folder, ICollection<string>? skip = null, Action<string>? log = null)
    {
        var list = new List<(string Lang, string Path)>();
        foreach (var p in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(p);
            if (!name.StartsWith("cliloc.", StringComparison.OrdinalIgnoreCase)) continue;
            string ext = name.Substring("cliloc.".Length).ToLowerInvariant();
            if (ext.Length is < 1 or > 4 || !ext.All(char.IsAsciiLetterOrDigit))
            {
                log?.Invoke($"skipping '{name}' (not a 1-4 letter language code)");
                continue;
            }
            if (skip != null && skip.Contains(ext))
            {
                log?.Invoke($"skipping '{name}' (skip list)");
                continue;
            }
            if (!IsClilocFile(p))
            {
                log?.Invoke($"skipping '{name}' (not a cliloc file: neither BWT-compressed nor a raw table that adds up)");
                continue;
            }
            if (list.Any(l => l.Lang == ext)) throw new InvalidDataException($"cliloc: two files for language '{ext}' (names differing only in case)");
            list.Add((ext, p));
        }
        if (list.Count == 0) throw new InvalidDataException($"cliloc: no Cliloc.* language files in '{folder}'");
        if (!list.Any(l => l.Lang == "enu")) throw new InvalidDataException($"cliloc: no Cliloc.enu in '{folder}' (the client reads it under every language)");
        return list.OrderBy(l => l.Lang == "enu" ? "" : l.Lang, StringComparer.Ordinal).ToList();
    }

    /// <summary>A cliloc file by content: BWT-compressed, or a 6-byte header then records (number i32, flag u8, length u16, text) ending exactly at the file's end.</summary>
    public static bool IsClilocFile(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 6) return false;
        if (b[3] == 0x8E) return true;
        long at = 6;
        int records = 0;
        while (at < b.Length)
        {
            if (at + 7 > b.Length) return false;
            int len = b[at + 5] | b[at + 6] << 8;
            at += 7 + len;
            records++;
        }
        return at == b.Length && records > 0;
    }
}
