using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Uo.Pack;

// text/cliloc.bin (docs/modern-client/pack-format.md, "text/cliloc.bin"): a record store with one record per language
// and at most one record of the shard's own strings (Clilocs.txt folded in by the converter). Every record holds a
// ClilocTable payload: sorted ids, string ends, and the UTF-8 strings as one blob (the record itself may be Brotli
// compressed whole, --cliloc-brotli). A block-compressed layout (meta2 bit 8) was tried and dropped (owner, 2026-10-31);
// a store using it is rejected.

/// <summary>
/// One cliloc table, the raw payload of a cliloc record. Flat layout:
/// <code>
///   count u32, blobBytes u32
///   ids  i32[count]    strictly ascending (binary search)
///   ends u32[count]    string i is blob[ends[i−1] (0 for i = 0) .. ends[i])
///   blob               UTF-8, strings back to back in id order
/// </code>
/// Strings are created on first use and cached; <see cref="TryGetString"/> is thread-safe, never throws, and returns the
/// same instance for an id every time.
/// </summary>
public sealed class ClilocTable
{
    public const int HeaderBytes = 8;
    /// <summary>Largest raw payload a reader accepts (the largest client file, Cliloc.jpn, is about 9 MiB raw).</summary>
    public const int MaxRawBytes = 256 << 20;
    private readonly byte[] _data;
    private readonly int _count;
    private readonly int _idsAt, _endsAt;
    private readonly int _blobAt;
    private readonly string?[] _cache;

    private ClilocTable(byte[] data, int count)
    {
        _data = data;
        _count = count;
        _idsAt = HeaderBytes;
        _endsAt = HeaderBytes + count * 4;
        _blobAt = HeaderBytes + count * 8;
        _cache = new string?[count];
    }
    public int Count => _count;

    /// <summary>Bytes held: the payload (index and UTF-8 text); strings made so far are extra.</summary>
    public long PayloadBytes => _data.Length;

    private ReadOnlySpan<int> Ids => MemoryMarshal.Cast<byte, int>(_data.AsSpan(_idsAt, _count * 4));
    private ReadOnlySpan<uint> Ends => MemoryMarshal.Cast<byte, uint>(_data.AsSpan(_endsAt, _count * 4));
    public int IdAt(int index) => Ids[index];

    /// <summary>Index of <paramref name="id"/>, or −1.</summary>
    public int IndexOf(int id)
    {
        int i = Ids.BinarySearch(id);
        return i >= 0 ? i : -1;
    }

    /// <summary>The UTF-8 bytes of string <paramref name="index"/>.</summary>
    public ReadOnlySpan<byte> Utf8At(int index)
    {
        var ends = Ends;
        uint start = index == 0 ? 0 : ends[index - 1];
        return _data.AsSpan(_blobAt + (int)start, (int)(ends[index] - start));
    }

    /// <summary>The string at <paramref name="index"/>, created on first use. Thread-safe.</summary>
    public string StringAt(int index)
    {
        var s = Volatile.Read(ref _cache[index]);
        if (s != null) return s;
        s = Encoding.UTF8.GetString(Utf8At(index));
        return Interlocked.CompareExchange(ref _cache[index], s, null) ?? s;
    }

    public bool TryGetString(int id, out string text)
    {
        int i = IndexOf(id);
        text = i < 0 ? null! : StringAt(i);
        return i >= 0;
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
        var t = new ClilocTable(data, (int)count);
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
        return new ClilocTable(data, entries.Length);
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
    /// <summary>
    /// meta2 bit 8: the retired block-compressed layout (dropped 2026-10-31, never shipped). A store with a record using
    /// it is rejected as unsupported, never read as flat.
    /// </summary>
    public const ushort RetiredLayoutBlocked = 0x100;

    /// <summary>
    /// Walks the slots in order and resolves every record they reach. Throws <see cref="InvalidDataException"/> (the
    /// whole store is unusable) for an unknown meta2 kind or bit (the retired block-compressed bit 8 included), a
    /// language record without a valid code, a language code reached twice, an overrides record with a code, or more than one overrides record. Records no slot reaches
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
            if ((r.Meta2 & RetiredLayoutBlocked) != 0)
                throw new InvalidDataException($"cliloc: record {rec} uses the block-compressed layout (meta2 bit 8), which is no longer supported; rebuild the pack's text with a current converter");
            if ((r.Meta2 & ~0xFF) != 0) throw new InvalidDataException($"cliloc: record {rec} has unknown meta2 0x{r.Meta2:X4}");
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
        if ((r.Meta2 & RetiredLayoutBlocked) != 0)
        {
            error = "the block-compressed layout (meta2 bit 8), which is no longer supported";
            return null;
        }
        if (r.RawBytes < ClilocTable.HeaderBytes || r.RawBytes > ClilocTable.MaxRawBytes ||
            (r.Codec == StoreConst.CodecBrotli && r.StoredBytes > BrotliEncoder.GetMaxCompressedLength((int)r.RawBytes)))
        {
            error = $"an impossible size ({r.RawBytes} bytes)";
            return null;
        }
        try
        {
            var data = new byte[r.RawBytes];
            store.Read(r, data);
            var table = ClilocTable.TryParse(data);
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
