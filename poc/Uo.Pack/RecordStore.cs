using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Uo.Pack;

// Record stores (docs/modern-client/pack-format.md, "Record stores"): content that is read one item at a time and is
// not sampled by the GPU from pack pages (gumps first; sounds, fonts and the other "not in the pack" types follow the
// same layout). One file per content type, read by offset through one handle kept open:
//
//   FileHeader          magic = the content type (GUMP for gumps), kind 0 (records carry their own compression)
//   StoreHeader         slotCount, recordCount, flags (type-specific), reserved
//   StoreSlot[slotCount]       per id: record index + 1 (0 = absent), a type-specific u16 (gumps: gump.def hue)
//   StoreRecord[recordCount]   per stored item: offset, stored and raw size, codec, three type-specific u16
//   payloads                   record payloads, back to back, each compressed on its own
//
// Several ids may share one record (aliases, identical content). The tables are read once at open (one read each);
// a record is one positional read plus its decode, into the caller's buffer, from any thread.

public static class StoreConst
{
    public const uint MagicGumps = 0x504D5547; // "GUMP"
    public const string GumpsFile = "gumps/gumps.bin";

    /// <summary>StoreRecord.Codec: the payload as is.</summary>
    public const ushort CodecRaw = 0;
    /// <summary>StoreRecord.Codec: one Brotli stream (quality 9, window 22 in the ship profile).</summary>
    public const ushort CodecBrotli = 1;

    /// <summary>Gumps StoreHeader.Flags bit 0: converted from gumpartLegacyMUL.uop (ClassicUO GumpsLoader.UseUOPGumps; the status gump's layout depends on it).</summary>
    public const uint GumpFlagUop = 1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StoreHeader
{
    public uint SlotCount;
    public uint RecordCount;
    public uint Flags;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StoreSlot
{
    /// <summary>Record index + 1; 0 = no such id.</summary>
    public uint Record;
    /// <summary>Type-specific. Gumps: the gump.def alias hue (0 = none).</summary>
    public ushort Arg;
    public ushort Reserved;

    public readonly bool Present => Record != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StoreRecord
{
    /// <summary>Payload offset from the start of the file.</summary>
    public ulong Offset;
    public uint StoredBytes;
    /// <summary>Payload size after decoding.</summary>
    public uint RawBytes;
    public ushort Codec;
    /// <summary>Type-specific. Gumps: width, height, 0.</summary>
    public ushort Meta0, Meta1, Meta2;
}

/// <summary>Writes a record store: <see cref="Add"/> payloads (already encoded), map ids to them, then <see cref="Write"/>.</summary>
public sealed class RecordStoreWriter
{
    private readonly List<(StoreRecord Record, byte[] Payload)> _records = new();

    public StoreSlot[] Slots { get; }

    public RecordStoreWriter(int slotCount) => Slots = new StoreSlot[slotCount];

    public int RecordCount => _records.Count;

    /// <summary>Adds a stored payload (<paramref name="stored"/> is the encoded bytes); returns its record index.</summary>
    public int Add(byte[] stored, uint rawBytes, ushort codec, ushort meta0, ushort meta1, ushort meta2 = 0)
    {
        _records.Add((new StoreRecord { StoredBytes = (uint)stored.Length, RawBytes = rawBytes, Codec = codec, Meta0 = meta0, Meta1 = meta1, Meta2 = meta2 }, stored));
        return _records.Count - 1;
    }

    /// <summary>Replaces record <paramref name="index"/>'s stored payload (raw size, codec and meta stay).</summary>
    public void SetPayload(int index, byte[] stored)
    {
        var (r, _) = _records[index];
        r.StoredBytes = (uint)stored.Length;
        _records[index] = (r, stored);
    }

    public void Write(string path, uint magic, uint flags)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new StoreRecord[_records.Count];
        ulong offset = (ulong)(StructIo.SizeOf<FileHeader>() + StructIo.SizeOf<StoreHeader>() + Slots.Length * StructIo.SizeOf<StoreSlot>() + records.Length * StructIo.SizeOf<StoreRecord>());
        for (int i = 0; i < records.Length; i++)
        {
            records[i] = _records[i].Record;
            records[i].Offset = offset;
            offset += records[i].StoredBytes;
        }
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, magic);
        var h = new StoreHeader { SlotCount = (uint)Slots.Length, RecordCount = (uint)records.Length, Flags = flags };
        StructIo.Write(fs, in h);
        StructIo.WriteSpan<StoreSlot>(fs, Slots);
        StructIo.WriteSpan<StoreRecord>(fs, records);
        foreach (var (_, payload) in _records) fs.Write(payload);
    }

    public static byte[] Encode(ReadOnlySpan<byte> raw, ushort codec, int quality = 9)
    {
        if (codec == StoreConst.CodecRaw) return raw.ToArray();
        var buf = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, buf, out int n, quality, 22)) throw new InvalidOperationException("brotli");
        return buf.AsSpan(0, n).ToArray();
    }
}

/// <summary>
/// A record store opened for reading. The tables are in memory; the file handle stays open until <see cref="Dispose"/>.
/// <see cref="Read"/> is thread-safe (positional reads, a stateless decoder, pooled compressed buffers).
/// </summary>
public sealed class RecordStore : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly long _length;

    public StoreHeader Header { get; }
    public StoreSlot[] Slots { get; }
    public StoreRecord[] Records { get; }

    private RecordStore(SafeFileHandle file, StoreHeader header, StoreSlot[] slots, StoreRecord[] records)
    {
        _file = file;
        _length = RandomAccess.GetLength(file);
        Header = header;
        Slots = slots;
        Records = records;
    }

    public static RecordStore Open(string path, uint magic)
    {
        var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            FileHeader fileHeader = default;
            StoreHeader header = default;
            ReadExactly(fh, MemoryMarshal.AsBytes(new Span<FileHeader>(ref fileHeader)), 0);
            if (fileHeader.Magic != magic || !PackConst.VersionSupported(fileHeader.Version) || fileHeader.Kind != FileKind.Raw)
                throw new InvalidDataException($"'{path}' is not a supported record store (magic 0x{fileHeader.Magic:X8}, version {fileHeader.Version}, kind {fileHeader.Kind})");
            long pos = StructIo.SizeOf<FileHeader>();
            ReadExactly(fh, MemoryMarshal.AsBytes(new Span<StoreHeader>(ref header)), pos);
            pos += StructIo.SizeOf<StoreHeader>();
            if (header.SlotCount > 1 << 24 || header.RecordCount > header.SlotCount)
                throw new InvalidDataException($"'{path}': bad store header");
            var slots = new StoreSlot[header.SlotCount];
            ReadExactly(fh, MemoryMarshal.AsBytes(slots.AsSpan()), pos);
            pos += (long)slots.Length * StructIo.SizeOf<StoreSlot>();
            var records = new StoreRecord[header.RecordCount];
            ReadExactly(fh, MemoryMarshal.AsBytes(records.AsSpan()), pos);
            var store = new RecordStore(fh, header, slots, records);
            store.Validate(path);
            return store;
        }
        catch
        {
            fh.Dispose();
            throw;
        }
    }

    private void Validate(string path)
    {
        foreach (ref readonly var s in Slots.AsSpan())
            if (s.Record > Records.Length) throw new InvalidDataException($"'{path}': slot points past the record table");
        foreach (ref readonly var r in Records.AsSpan())
            if (r.Offset + r.StoredBytes > (ulong)_length || (r.Codec == StoreConst.CodecRaw && r.StoredBytes != r.RawBytes) || r.Codec > StoreConst.CodecBrotli)
                throw new InvalidDataException($"'{path}': bad record");
    }

    public int SlotCount => Slots.Length;

    /// <summary>The slot of <paramref name="id"/> and its record, or false when the id has none.</summary>
    public bool TryGet(int id, out StoreSlot slot, out StoreRecord record)
    {
        if ((uint)id < (uint)Slots.Length && Slots[id].Record != 0)
        {
            slot = Slots[id];
            record = Records[slot.Record - 1];
            return true;
        }
        slot = default;
        record = default;
        return false;
    }

    /// <summary>Decodes <paramref name="record"/>'s payload into <paramref name="dst"/> (at least RawBytes long). Thread-safe.</summary>
    public void Read(in StoreRecord record, Span<byte> dst)
    {
        var raw = dst.Slice(0, (int)record.RawBytes);
        if (record.Codec == StoreConst.CodecRaw)
        {
            ReadExactly(_file, raw, (long)record.Offset);
            return;
        }
        var compressed = ArrayPool<byte>.Shared.Rent((int)record.StoredBytes);
        try
        {
            var src = compressed.AsSpan(0, (int)record.StoredBytes);
            ReadExactly(_file, src, (long)record.Offset);
            if (!BrotliDecoder.TryDecompress(src, raw, out int written) || written != raw.Length)
                throw new InvalidDataException($"corrupt record at offset {record.Offset}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    private static void ReadExactly(SafeFileHandle fh, Span<byte> dst, long offset)
    {
        while (!dst.IsEmpty)
        {
            int n = RandomAccess.Read(fh, dst, offset);
            if (n <= 0) throw new EndOfStreamException();
            dst = dst.Slice(n);
            offset += n;
        }
    }

    public void Dispose() => _file.Dispose();
}
