using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Uo.Pack;

// The last files the client read from the UO folder (docs/dev/pack-format.md, "Skills, skill groups, multimap, string
// dictionary" and "Loose text files"):
//
//   skills.bin           skills.mul + Skills.idx: a string list, one entry per skill in the client's order
//   skillgrp.bin         skillgrp.mul: the group names and the per-skill group numbers as the file stores them
//   multimap.bin         Multimap.rle and facet0*.mul: a record store of the files' RLE streams
//   text/stringdict.bin  string_dictionary.uop: a string list
//   loose/<name>         text and def files shards edit by hand, copied verbatim (lower-case names)
//
// Each is optional: a file the source install lacks has no table, and the client treats it as it treated the missing
// file. The readers treat their input as untrusted: sizes are checked before anything is allocated, and a bad file is
// reported with InvalidDataException.

public static class MiscConst
{
    public const uint MagicSkills = 0x534C4B53;      // "SKLS"
    public const uint MagicSkillGroups = 0x52474B53; // "SKGR"
    public const uint MagicMultiMap = 0x50414D4D;    // "MMAP"
    public const uint MagicStringDict = 0x44525453;  // "STRD"

    public const string SkillsFile = "skills.bin";
    public const string SkillGroupsFile = "skillgrp.bin";
    public const string MultiMapFile = "multimap.bin";
    public const string StringDictFile = "text/stringdict.bin";
    public const string LooseDir = "loose";

    /// <summary>skills.bin entry value bit 0: the skill has a use button.</summary>
    public const uint SkillHasAction = 1;

    /// <summary>multimap.bin slot 0: Multimap.rle.</summary>
    public const int MultiMapRleSlot = 0;
    /// <summary>multimap.bin slot of the n-th facet image (facet0*.mul in ordinal name order, as the client indexed them).</summary>
    public static int MultiMapFacetSlot(int n) => 1 + n;
    /// <summary>Facet image slots a multimap store may hold (the client knows six facets; the folder may hold more files).</summary>
    public const int MaxMultiMapFacets = 255;

    /// <summary>
    /// The loose files the converter copies (case-insensitive in the install, lower-case in the pack). The client finds
    /// one with <c>loose/</c> + the lower-case name, so the list is the converter's; a file not listed here is not packed.
    /// </summary>
    public static readonly string[] LooseFiles =
    [
        "prof.txt", "citytext.enu", "stitchin.def", "anim1.def", "anim2.def",
        "walls.txt", "floors.txt", "doors.txt", "misc.txt", "stairs.txt", "teleprts.txt", "roof.txt", "suppinfo.txt",
    ];

    /// <summary>The pack-relative path of a loose file: <c>loose/</c> + its lower-case name.</summary>
    public static string LoosePath(string name) => LooseDir + "/" + name.ToLowerInvariant();

    /// <summary>Largest loose file the converter copies (the biggest stock one, stitchin.def, is 230 KB).</summary>
    public const long MaxLooseBytes = 16 << 20;

    /// <summary>
    /// Largest multimap record (decoded): readers check it before allocating. The stock files are at most 12 MB
    /// (facet01.mul, 7168×4096 run-length coded), so 64 MiB leaves room for larger shard maps.
    /// </summary>
    public const int MaxMultiMapRecordBytes = 64 << 20;

    public static RecordStore OpenMultiMapStore(string path) =>
        RecordStore.Open(path, MagicMultiMap, 1, 1 + MaxMultiMapFacets);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StringListRecord
{
    /// <summary>Content-specific (skills.bin: <see cref="MiscConst.SkillHasAction"/>; text/stringdict.bin: 0).</summary>
    public uint Value;
    /// <summary>End of this entry's text in the blob; the text starts at the previous entry's end (0 for the first).</summary>
    public uint End;
}

/// <summary>
/// A list of strings with a u32 value each, kept as a record store (<see cref="RecordStore"/>) with exactly one slot
/// whose record (raw or Brotli) is the list: count u32, blobBytes u32, <c>StringListRecord[count]</c>, then the UTF-8
/// blob. skills.bin (raw) and text/stringdict.bin (Brotli).
/// </summary>
public static class StringListFile
{
    public const int MaxEntries = 1 << 20;
    public const int MaxBlobBytes = 64 << 20;
    /// <summary>Largest valid list: <see cref="MaxEntries"/> records and <see cref="MaxBlobBytes"/> of text.</summary>
    public const long MaxRawBytes = 8 + MaxEntries * 8L + MaxBlobBytes;

    public static byte[] Encode(IReadOnlyList<(uint Value, string Text)> entries)
    {
        if (entries.Count > MaxEntries) throw new ArgumentException($"{entries.Count} entries; at most {MaxEntries}");
        var utf8 = entries.Select(e => Encoding.UTF8.GetBytes(e.Text)).ToArray();
        long blob = utf8.Sum(b => (long)b.Length);
        if (blob > MaxBlobBytes) throw new ArgumentException($"{blob} bytes of text; at most {MaxBlobBytes}");
        var buf = new byte[8 + entries.Count * 8 + blob];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)blob);
        var records = MemoryMarshal.Cast<byte, StringListRecord>(buf.AsSpan(8, entries.Count * 8));
        int at = 8 + entries.Count * 8;
        uint end = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            end += (uint)utf8[i].Length;
            records[i] = new StringListRecord { Value = entries[i].Value, End = end };
            utf8[i].CopyTo(buf, at);
            at += utf8[i].Length;
        }
        return buf;
    }

    /// <summary>Decodes a list payload. Throws <see cref="InvalidDataException"/> when the sizes or ends do not add up.</summary>
    public static (uint Value, string Text)[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) throw new InvalidDataException("string list: too short");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint blob = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
        if (count > MaxEntries || blob > MaxBlobBytes || 8 + count * 8UL + blob != (ulong)payload.Length)
            throw new InvalidDataException("string list: bad sizes");
        var records = MemoryMarshal.Cast<byte, StringListRecord>(payload.Slice(8, (int)count * 8));
        var text = payload.Slice(8 + (int)count * 8);
        var result = new (uint, string)[count];
        uint start = 0;
        for (int i = 0; i < records.Length; i++)
        {
            uint end = records[i].End;
            if (end < start || end > blob) throw new InvalidDataException($"string list: entry {i} out of range");
            result[i] = (records[i].Value, Encoding.UTF8.GetString(text.Slice((int)start, (int)(end - start))));
            start = end;
        }
        if (start != blob) throw new InvalidDataException("string list: blob size mismatch");
        return result;
    }

    public static void Write(string path, uint magic, IReadOnlyList<(uint Value, string Text)> entries, bool brotli)
    {
        var raw = Encode(entries);
        ushort codec = brotli ? StoreConst.CodecBrotli : StoreConst.CodecRaw;
        var payload = RecordStoreWriter.Encode(raw, codec);
        if (brotli && payload.Length >= raw.Length)
        {
            codec = StoreConst.CodecRaw;
            payload = raw;
        }
        var w = new RecordStoreWriter(1);
        int rec = w.Add(payload, (uint)raw.Length, codec, 0, 0);
        w.Slots[0] = new StoreSlot { Record = (uint)rec + 1 };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        w.Write(path, magic, 0);
    }

    /// <summary>Reads every entry in order. Throws <see cref="InvalidDataException"/> when the file is not a valid list.</summary>
    public static (uint Value, string Text)[] Read(string path, uint magic)
    {
        using var store = RecordStore.Open(path, magic, 1, 1);
        if (!store.TryGet(0, out _, out var rec)) throw new InvalidDataException($"'{path}': no list record");
        if (rec.RawBytes > MaxRawBytes) throw new InvalidDataException($"'{path}': {rec.RawBytes} bytes; a list is at most {MaxRawBytes}");
        var raw = new byte[rec.RawBytes];
        store.Read(rec, raw);
        try
        {
            return Decode(raw);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"'{path}': {e.Message}", e);
        }
    }
}

/// <summary>Bounded reads of the small plain tables in this file.</summary>
internal static class BoundedRead
{
    // Reads the FileHeader and a fixed-size head first; the whole file is read only when sizesOk (given the head and the
    // length after the FileHeader) finds the head's sizes bounded and matching the file's exact length (a forged or
    // oversized file must not be read whole).
    internal static byte[] ReadBounded(string path, uint magic, int headBytes, out int hdr, Func<ReadOnlySpan<byte>, long, bool> sizesOk)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        hdr = StructIo.SizeOf<FileHeader>();
        Span<byte> head = stackalloc byte[hdr + headBytes];
        long length = fs.Length;
        if (length < head.Length) throw new InvalidDataException($"'{path}' is too short");
        fs.ReadExactly(head);
        var fh = MemoryMarshal.Read<FileHeader>(head);
        if (fh.Magic != magic || !PackConst.VersionSupported(fh.Version) || fh.Kind != FileKind.Raw)
            throw new InvalidDataException($"'{path}': magic 0x{fh.Magic:X8}, version {fh.Version}, kind {fh.Kind}; expected magic 0x{magic:X8}");
        if (!sizesOk(head.Slice(hdr), length - hdr)) throw new InvalidDataException($"'{path}': bad sizes");
        var bytes = new byte[length];
        head.CopyTo(bytes);
        fs.ReadExactly(bytes.AsSpan(head.Length));
        return bytes;
    }
}

/// <summary>
/// skillgrp.bin: <c>FileHeader</c>, nameCount u32, assignmentCount u32, blobBytes u32, u32 nameEnd[nameCount],
/// i32 assignment[assignmentCount], then the UTF-8 blob of the names.
///
/// The names are the file's groups 1..count−1 (group 0 is the client's "Miscellaneous", not stored in the file); the
/// assignments are the file's trailing i32 group numbers, one per skill in order, unchecked: the client applies its own
/// rules to them (a number past the last group skips the entry; the skill count is the one known at run time).
/// </summary>
public static class SkillGroupsFile
{
    public const int MaxNames = 1 << 16;
    public const int MaxAssignments = 1 << 16;
    public const int MaxBlobBytes = 1 << 20;

    public static void Write(string path, IReadOnlyList<string> names, IReadOnlyList<int> assignments)
    {
        if (names.Count > MaxNames || assignments.Count > MaxAssignments) throw new ArgumentException("too many skill groups or assignments");
        var utf8 = names.Select(Encoding.UTF8.GetBytes).ToArray();
        long blob = utf8.Sum(b => (long)b.Length);
        if (blob > MaxBlobBytes) throw new ArgumentException($"{blob} bytes of group names; at most {MaxBlobBytes}");
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, MiscConst.MagicSkillGroups);
        StructIo.Write(fs, (uint)names.Count);
        StructIo.Write(fs, (uint)assignments.Count);
        StructIo.Write(fs, (uint)blob);
        uint end = 0;
        foreach (var b in utf8)
        {
            end += (uint)b.Length;
            StructIo.Write(fs, end);
        }
        foreach (int a in assignments) StructIo.Write(fs, a);
        foreach (var b in utf8) fs.Write(b);
        fs.Commit();
    }

    public static (string[] Names, int[] Assignments) Read(string path)
    {
        var bytes = BoundedRead.ReadBounded(path, MiscConst.MagicSkillGroups, 12, out int hdr, (head, length) =>
        {
            uint names = BinaryPrimitives.ReadUInt32LittleEndian(head);
            uint assigns = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4));
            uint blob = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(8));
            return names <= MaxNames && assigns <= MaxAssignments && blob <= MaxBlobBytes &&
                   12 + names * 4UL + assigns * 4UL + blob == (ulong)length;
        });
        int nameCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(hdr));
        int assignCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(hdr + 4));
        uint blobBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(hdr + 8));
        int at = hdr + 12;
        var ends = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(at, nameCount * 4));
        at += nameCount * 4;
        var assignments = MemoryMarshal.Cast<byte, int>(bytes.AsSpan(at, assignCount * 4)).ToArray();
        at += assignCount * 4;
        var names = new string[nameCount];
        uint start = 0;
        for (int i = 0; i < nameCount; i++)
        {
            if (ends[i] < start || ends[i] > blobBytes) throw new InvalidDataException($"'{path}': group name {i} out of range");
            names[i] = Encoding.UTF8.GetString(bytes, at + (int)start, (int)(ends[i] - start));
            start = ends[i];
        }
        if (start != blobBytes) throw new InvalidDataException($"'{path}': blob size mismatch");
        return (names, assignments);
    }
}
