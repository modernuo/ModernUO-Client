using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Uo.Pack;

// Format 1.0 completeness tables: the data ClassicUO still read from MUL/UOP besides pages and regions. See "Tile data" and
// "Game data tables" in docs/modern-client/pack-format.md.

public static class TableConst
{
    /// <summary>tiledata.bin, the single tile data file (pack 1.1).</summary>
    public const uint MagicTileData = 0x454C4954;    // "TILE"
    /// <summary>Retired: tiledata_ext.bin (pack 1.0), replaced by tiledata.bin.</summary>
    public const uint MagicTileDataExtRetired = 0x58444C54; // "TLDX"
    public const uint MagicHueRaw = 0x57524548;      // "HERW"
    public const uint MagicRadarCol = 0x43444152;    // "RADC"
    public const uint MagicMultis = 0x49544C4D;      // "MLTI"
    public const uint MagicLights = 0x5448474C;      // "LGHT"
    public const uint MagicTileArt = 0x54524154;     // "TART"
    public const uint MagicEquipConv = 0x56435145;   // "EQCV"

    public const string TileDataFile = "tiledata.bin";
    /// <summary>Retired pack 1.0 file name; the converter deletes a stale copy from its output.</summary>
    public const string TileDataExtFileRetired = "tiledata_ext.bin";
    public const string HueRawFile = "hues_raw.bin";
    public const string RadarColFile = "radarcol.bin";
    public const string MultisFile = "multis.bin";
    public const string LightsFile = "lights.bin";
    public const string TileArtFile = "tileart.bin";
    public const string EquipConvFile = "equipconv.bin";
}

/// <summary>Canonical 5->8 bit expansion: bit replication (c << 3) | (c >> 2), what GPUs return for B5G5R5A1_UNORM.</summary>
public static class PackColor
{
    public static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));

    /// <summary>15-bit UO colour (bits 0-4 B, 5-9 G, 10-14 R) to RGBA8 (R in the low byte), alpha 0.</summary>
    public static uint ToRgb8(ushort c) =>
        Expand5((c >> 10) & 0x1F) | (uint)Expand5((c >> 5) & 0x1F) << 8 | (uint)Expand5(c & 0x1F) << 16;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LandTileInfo
{
    public ulong Flags;
    public uint NameOffset;
    public ushort NameLength;
    public ushort TexId;
    public TilePackFlags PackFlags;
    public ushort Pad;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticTileInfo
{
    public ulong Flags;
    /// <summary>tiledata "quantity" (ClassicUO StaticTiles.Count).</summary>
    public int Count;
    public uint NameOffset;
    public ushort NameLength;
    public ushort AnimId;
    public ushort Hue;
    public ushort LightIndex;
    public byte Weight;
    public byte Layer;
    public byte Height;
    public byte Pad;
    public TilePackFlags PackFlags;
    public ushort Pad2;
}

/// <summary>
/// tiledata.bin, the pack's only tile data file: landCount i32, staticCount i32, nameBytes i32, LandTileInfo[landCount],
/// StaticTileInfo[staticCount], then the UTF-8 name table. Names are referenced by (offset, length) into the table;
/// duplicates share bytes. The renderer's 12-byte arrays are built from it at load (<see cref="Compact"/>).
/// </summary>
public static class TileDataFile
{
    public const int LandEntryBytes = 20, StaticEntryBytes = 32;
    /// <summary>Longest name reference accepted, in UTF-8 bytes. Source tiledata names are 20 single-byte characters (at most
    /// 40 bytes as UTF-8); the cap keeps an untrusted file from making the reader decode huge strings.</summary>
    public const int MaxNameBytes = 64;
    /// <summary>Largest name table accepted: every entry with its own longest name (5 MiB).</summary>
    public const int MaxNameTableBytes = (PackConst.LandTileCount + PackConst.StaticTileCount) * MaxNameBytes;

    /// <summary>Writes the file. The counts must be exactly <see cref="PackConst.LandTileCount"/> and
    /// <see cref="PackConst.StaticTileCount"/> (the standardized format); a caller with fewer source entries pads them.</summary>
    public static void Write(string path, ReadOnlySpan<LandTileInfo> land, ReadOnlySpan<StaticTileInfo> statics, ReadOnlySpan<byte> names)
    {
        if (land.Length != PackConst.LandTileCount) throw new ArgumentException($"{land.Length} land entries, the format has exactly {PackConst.LandTileCount}", nameof(land));
        if (statics.Length != PackConst.StaticTileCount) throw new ArgumentException($"{statics.Length} static entries, the format has exactly {PackConst.StaticTileCount}", nameof(statics));
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, TableConst.MagicTileData);
        StructIo.Write(fs, land.Length);
        StructIo.Write(fs, statics.Length);
        StructIo.Write(fs, names.Length);
        StructIo.WriteSpan(fs, land);
        StructIo.WriteSpan(fs, statics);
        fs.Write(names);
        fs.Commit();
    }

    /// <summary>Reads and validates the file (untrusted input): magic and version, land count = 0x4000 and static count =
    /// 0x10000 exactly (<see cref="PackConst.LandTileCount"/>, <see cref="PackConst.StaticTileCount"/>), the name table at most <see cref="MaxNameTableBytes"/>, the exact file size, and every name reference
    /// inside the name table and at most <see cref="MaxNameBytes"/> long (checked before the name table is allocated).</summary>
    public static (LandTileInfo[] Land, StaticTileInfo[] Statics, byte[] Names) Read(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < StructIo.SizeOf<FileHeader>() + 12) throw new InvalidDataException($"{path}: truncated ({fs.Length} bytes)");
        var h = StructIo.Read<FileHeader>(fs);
        if (h.Magic == PackConst.MagicTileDataCompactRetired || h.Magic == TableConst.MagicTileDataExtRetired)
            throw new InvalidDataException($"{path}: pack 1.0 tile data layout (magic 0x{h.Magic:X8}). Tile data is one file since pack 1.1: " +
                "convert the pack again (Uo.Converter --only tiledata).");
        if (h.Magic != TableConst.MagicTileData) throw new InvalidDataException($"{path}: bad magic 0x{h.Magic:X8}, expected 0x{TableConst.MagicTileData:X8}");
        if (!PackConst.VersionSupported(h.Version)) throw new InvalidDataException($"{path}: unsupported pack version {h.Version}");
        if (h.Kind != 0) throw new InvalidDataException($"{path}: compression kind {h.Kind}, expected 0 (raw)");
        int landCount = StructIo.Read<int>(fs), staticCount = StructIo.Read<int>(fs), nameBytes = StructIo.Read<int>(fs);
        if (landCount != PackConst.LandTileCount) throw new InvalidDataException($"{path}: {landCount} land entries, expected {PackConst.LandTileCount}");
        if (staticCount != PackConst.StaticTileCount) throw new InvalidDataException($"{path}: {staticCount} static entries, expected {PackConst.StaticTileCount}");
        if (nameBytes < 0 || nameBytes > MaxNameTableBytes) throw new InvalidDataException($"{path}: name table size {nameBytes} (0..{MaxNameTableBytes})");
        long expected = StructIo.SizeOf<FileHeader>() + 12L + (long)landCount * LandEntryBytes + (long)staticCount * StaticEntryBytes + nameBytes;
        if (fs.Length != expected) throw new InvalidDataException($"{path}: {fs.Length} bytes, expected {expected} from its counts");
        var land = new LandTileInfo[landCount];
        var statics = new StaticTileInfo[staticCount];
        StructIo.ReadSpan<LandTileInfo>(fs, land);
        StructIo.ReadSpan<StaticTileInfo>(fs, statics);
        for (int i = 0; i < land.Length; i++)
            CheckName(path, "land", i, land[i].NameOffset, land[i].NameLength, nameBytes);
        for (int i = 0; i < statics.Length; i++)
            CheckName(path, "static", i, statics[i].NameOffset, statics[i].NameLength, nameBytes);
        var names = new byte[nameBytes];
        fs.ReadExactly(names);
        return (land, statics, names);
    }

    static void CheckName(string path, string kind, int i, uint offset, ushort length, int nameBytes)
    {
        if (length > MaxNameBytes) throw new InvalidDataException($"{path}: {kind} {i} name is {length} bytes (at most {MaxNameBytes})");
        if ((ulong)offset + length > (ulong)nameBytes) throw new InvalidDataException($"{path}: {kind} {i} name outside the name table");
    }

    /// <summary>Decodes every entry's name, once per distinct (offset, length) reference: entries that share a reference share
    /// the string. With the reader's caps, at most 81,920 distinct names of at most 64 bytes.</summary>
    public static (string[] Land, string[] Statics) DecodeNames(ReadOnlySpan<LandTileInfo> land, ReadOnlySpan<StaticTileInfo> statics, byte[] names)
    {
        var cache = new Dictionary<ulong, string>();
        string Get(uint offset, ushort length)
        {
            if (length == 0) return "";
            ulong key = (ulong)offset << 16 | length;
            if (!cache.TryGetValue(key, out var s)) cache[key] = s = Name(names, offset, length);
            return s;
        }
        var l = new string[land.Length];
        for (int i = 0; i < l.Length; i++) l[i] = Get(land[i].NameOffset, land[i].NameLength);
        var st = new string[statics.Length];
        for (int i = 0; i < st.Length; i++) st[i] = Get(statics[i].NameOffset, statics[i].NameLength);
        return (l, st);
    }

    /// <summary>The renderer's compact arrays (12 bytes per entry), built from the full entries.</summary>
    public static (LandTileData[] Land, StaticTileData[] Statics) Compact(ReadOnlySpan<LandTileInfo> land, ReadOnlySpan<StaticTileInfo> statics)
    {
        var l = new LandTileData[land.Length];
        for (int i = 0; i < l.Length; i++)
        {
            ref readonly var t = ref land[i];
            l[i] = new LandTileData { Flags = t.Flags, TexId = t.TexId, PackFlags = t.PackFlags };
        }
        var s = new StaticTileData[statics.Length];
        for (int i = 0; i < s.Length; i++)
        {
            ref readonly var t = ref statics[i];
            s[i] = new StaticTileData { Flags = t.Flags, Height = t.Height, Layer = t.Layer, PackFlags = t.PackFlags };
        }
        return (l, s);
    }

    /// <summary><see cref="Read"/> then <see cref="Compact"/>.</summary>
    public static (LandTileData[] Land, StaticTileData[] Statics) ReadCompact(string path)
    {
        var (land, statics, _) = Read(path);
        return Compact(land, statics);
    }

    public static string Name(byte[] names, uint offset, ushort length) => length == 0 ? "" : Encoding.UTF8.GetString(names, (int)offset, length);

    /// <summary>Builds a deduplicated UTF-8 name table; returns (offset, length) per input name.</summary>
    public static byte[] BuildNames(IReadOnlyList<string?> names, out (uint Offset, ushort Length)[] refs)
    {
        var ms = new MemoryStream();
        var seen = new Dictionary<string, (uint, ushort)>(StringComparer.Ordinal);
        refs = new (uint, ushort)[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            var n = names[i] ?? "";
            if (n.Length == 0) continue;
            if (!seen.TryGetValue(n, out var r))
            {
                var b = Encoding.UTF8.GetBytes(n);
                if (b.Length > MaxNameBytes) throw new InvalidDataException($"tile name \"{n}\" is {b.Length} bytes (at most {MaxNameBytes})");
                seen[n] = r = ((uint)ms.Length, (ushort)b.Length);
                ms.Write(b);
            }
            refs[i] = r;
        }
        return ms.ToArray();
    }
}

[InlineArray(32)]
public struct HueColors
{
    private ushort _e;
}

[InlineArray(20)]
public struct HueName
{
    private byte _e;
}

/// <summary>One hues.mul entry, byte-identical to ClassicUO's HuesBlock (88 bytes). Hue h (1-based) is entry h - 1.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HueEntry
{
    /// <summary>32 shades, 15-bit colour (bits 0-4 B, 5-9 G, 10-14 R; bit 15 as stored).</summary>
    public HueColors Colors;
    public ushort TableStart;
    public ushort TableEnd;
    /// <summary>Raw name bytes, NUL-padded.</summary>
    public HueName Name;

    public readonly string NameString()
    {
        ReadOnlySpan<byte> b = Name;
        int n = b.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? b : b[..n]);
    }
}

/// <summary>A file of FileHeader, count i32, T[count]. Used by the simple tables.</summary>
public static class TableFile
{
    public static void Write<T>(string path, uint magic, ReadOnlySpan<T> entries) where T : unmanaged
    {
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, magic);
        StructIo.Write(fs, entries.Length);
        StructIo.WriteSpan(fs, entries);
        fs.Commit();
    }

    public static T[] Read<T>(string path, uint magic) where T : unmanaged
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, magic);
        var entries = new T[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<T>(fs, entries);
        return entries;
    }
}

/// <summary>hues_raw.bin: count i32, HueEntry[count]. The pack's only hue file: the RGBA8 LUT is built from it at load
/// (<see cref="HueLut"/>).</summary>
public static class HueRawFile
{
    /// <summary>Hue ids are u16, so no valid file has more entries than this.</summary>
    public const int MaxEntries = 0x10000;

    public static void Write(string path, ReadOnlySpan<HueEntry> entries) => TableFile.Write(path, TableConst.MagicHueRaw, entries);

    /// <summary>Reads the file as untrusted input: the count must be 0..<see cref="MaxEntries"/> and fit in the file.</summary>
    public static HueEntry[] Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, TableConst.MagicHueRaw);
        int count = StructIo.Read<int>(fs);
        if (count < 0 || count > MaxEntries || (long)count * Unsafe.SizeOf<HueEntry>() > fs.Length - fs.Position)
            throw new InvalidDataException($"{TableConst.HueRawFile}: bad entry count {count} for a {fs.Length}-byte file");
        var entries = new HueEntry[count];
        StructIo.ReadSpan<HueEntry>(fs, entries);
        return entries;
    }
}

/// <summary>The RGBA8 hue LUT the renderers upload (one row per hue, 32 shades), built from <see cref="HueRawFile"/>
/// entries at load. Canonical format-1.0 expansion: row i = hue i + 1, column = shade, each 15-bit colour bit-replicated
/// to 8 bits per channel (<see cref="PackColor.ToRgb8"/>: x &lt;&lt; 3 | x &gt;&gt; 2, bit 15 ignored), R in the low byte,
/// alpha 0xFF. Packs before the one-source-of-truth change also shipped this table as hues.bin; it is no longer read or
/// written.</summary>
public static class HueLut
{
    public const int Shades = 32;

    public static uint[] Build(ReadOnlySpan<HueEntry> entries)
    {
        if (entries.Length > HueRawFile.MaxEntries)
            throw new InvalidDataException($"hue LUT: {entries.Length} entries, at most {HueRawFile.MaxEntries}");
        var lut = new uint[entries.Length * Shades];
        Build(entries, lut);
        return lut;
    }

    /// <summary>Fills the first entries.Length * 32 values of <paramref name="dest"/>.</summary>
    public static void Build(ReadOnlySpan<HueEntry> entries, Span<uint> dest)
    {
        if (dest.Length < entries.Length * Shades) throw new ArgumentException("destination too small", nameof(dest));
        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlySpan<ushort> colors = entries[i].Colors;
            var row = dest.Slice(i * Shades, Shades);
            for (int s = 0; s < Shades; s++) row[s] = PackColor.ToRgb8(colors[s]) | 0xFF000000u;
        }
    }
}

/// <summary>radarcol.bin: count i32, u16[count] 15-bit colours. Index = land id (0..0x3FFF) or 0x4000 + static graphic.</summary>
public static class RadarColFile
{
    public static void Write(string path, ReadOnlySpan<ushort> colors) => TableFile.Write(path, TableConst.MagicRadarCol, colors);
    public static ushort[] Read(string path) => TableFile.Read<ushort>(path, TableConst.MagicRadarCol);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MultiEntry
{
    public uint FirstComponent;
    /// <summary>0 = no such multi.</summary>
    public uint ComponentCount;
}

[Flags]
public enum MultiComponentFlags : ushort
{
    None = 0,
    /// <summary>ClassicUO MultiInfo.IsVisible.</summary>
    Visible = 1 << 0,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MultiComponent
{
    public ushort Graphic;
    public short X, Y, Z;
    public MultiComponentFlags Flags;
}

/// <summary>multis.bin: multiCount i32, componentCount i32, MultiEntry[multiCount] (index = multi id), MultiComponent[componentCount].</summary>
public static class MultiFile
{
    public static void Write(string path, ReadOnlySpan<MultiEntry> multis, ReadOnlySpan<MultiComponent> components)
    {
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, TableConst.MagicMultis);
        StructIo.Write(fs, multis.Length);
        StructIo.Write(fs, components.Length);
        StructIo.WriteSpan(fs, multis);
        StructIo.WriteSpan(fs, components);
        fs.Commit();
    }

    public static (MultiEntry[] Multis, MultiComponent[] Components) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, TableConst.MagicMultis);
        var multis = new MultiEntry[StructIo.Read<int>(fs)];
        var components = new MultiComponent[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<MultiEntry>(fs, multis);
        StructIo.ReadSpan<MultiComponent>(fs, components);
        return (multis, components);
    }

    public static ReadOnlySpan<MultiComponent> Components(MultiEntry[] multis, MultiComponent[] components, int id) =>
        (uint)id < (uint)multis.Length ? components.AsSpan((int)multis[id].FirstComponent, (int)multis[id].ComponentCount) : default;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LightEntry
{
    /// <summary>Byte offset of the light's texels in the data block.</summary>
    public uint Offset;
    /// <summary>0 x 0 = no such light.</summary>
    public ushort Width, Height;
}

/// <summary>
/// lights.bin: count i32, dataBytes i32, LightEntry[count] (index = light id), then the texels: one light.mul byte per
/// texel, row-major. Values 0..31 are intensities; values above 31 are negative lights, which ClassicUO folds to
/// ~v &amp; 31 (see <see cref="Intensity"/>).
/// </summary>
public static class LightFile
{
    public static void Write(string path, ReadOnlySpan<LightEntry> lights, ReadOnlySpan<byte> data)
    {
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, TableConst.MagicLights);
        StructIo.Write(fs, lights.Length);
        StructIo.Write(fs, data.Length);
        StructIo.WriteSpan(fs, lights);
        fs.Write(data);
        fs.Commit();
    }

    public static (LightEntry[] Lights, byte[] Data) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, TableConst.MagicLights);
        var lights = new LightEntry[StructIo.Read<int>(fs)];
        var data = new byte[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<LightEntry>(fs, lights);
        fs.ReadExactly(data);
        return (lights, data);
    }

    /// <summary>ClassicUO LightsLoader: intensity 0..31 of a stored texel.</summary>
    public static int Intensity(byte v) => v > 0x1F ? ~v & 0x1F : v;

    /// <summary>ClassicUO LightsLoader.GetLight texel: gray (i &lt;&lt; 3) with alpha 0xFF, or 0 when the intensity is 0.</summary>
    public static uint ToRgba8(byte v)
    {
        uint i = (uint)Intensity(v);
        return i == 0 ? 0 : (i << 19) | (i << 11) | (i << 3) | 0xFF000000;
    }
}

/// <summary>
/// A tileart.uop paperdoll appearance: ClassicUO TileArtInfo.TryGetAppearance(body) for item <see cref="Graphic"/>.
/// Only items whose record has more than one appearance subtype are listed (subtype 0's map), as ClassicUO requires.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TileArtAppearance
{
    public ushort Graphic;
    public ushort Body;
    public uint Appearance;
}

/// <summary>tileart.bin: count i32, TileArtAppearance[count] sorted by (Graphic, Body).</summary>
public static class TileArtFile
{
    public static void Write(string path, ReadOnlySpan<TileArtAppearance> entries) => TableFile.Write(path, TableConst.MagicTileArt, entries);
    public static TileArtAppearance[] Read(string path) => TableFile.Read<TileArtAppearance>(path, TableConst.MagicTileArt);

    public static bool TryGetAppearance(TileArtAppearance[] entries, ushort graphic, ushort body, out uint appearance)
    {
        int lo = 0, hi = entries.Length - 1;
        uint key = (uint)graphic << 16 | body;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint k = (uint)entries[mid].Graphic << 16 | entries[mid].Body;
            if (k == key) { appearance = entries[mid].Appearance; return true; }
            if (k < key) lo = mid + 1; else hi = mid - 1;
        }
        appearance = 0;
        return false;
    }
}

/// <summary>Equipconv.def row as ClassicUO resolves it (EquipConvData): Gump already defaulted from Graphic/NewGraphic.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EquipConvEntry
{
    public ushort Body;
    public ushort Graphic;
    public ushort NewGraphic;
    public ushort Gump;
    public ushort Color;
}

/// <summary>equipconv.bin: count i32, EquipConvEntry[count] sorted by (Body, Graphic), unique.</summary>
public static class EquipConvFile
{
    public static void Write(string path, ReadOnlySpan<EquipConvEntry> entries) => TableFile.Write(path, TableConst.MagicEquipConv, entries);
    public static EquipConvEntry[] Read(string path) => TableFile.Read<EquipConvEntry>(path, TableConst.MagicEquipConv);

    public static bool TryGet(EquipConvEntry[] entries, ushort body, ushort graphic, out EquipConvEntry entry)
    {
        int lo = 0, hi = entries.Length - 1;
        uint key = (uint)body << 16 | graphic;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint k = (uint)entries[mid].Body << 16 | entries[mid].Graphic;
            if (k == key) { entry = entries[mid]; return true; }
            if (k < key) lo = mid + 1; else hi = mid - 1;
        }
        entry = default;
        return false;
    }
}
