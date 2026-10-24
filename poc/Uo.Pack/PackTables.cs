using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Uo.Pack;

// Format 1.0 completeness tables: the data ClassicUO still read from MUL/UOP besides pages, regions and the compact
// tiledata/hue tables. See "Game data tables" in docs/modern-client/pack-format.md.

public static class TableConst
{
    public const uint MagicTileDataExt = 0x58444C54; // "TLDX"
    public const uint MagicHueRaw = 0x57524548;      // "HERW"
    public const uint MagicRadarCol = 0x43444152;    // "RADC"
    public const uint MagicMultis = 0x49544C4D;      // "MLTI"
    public const uint MagicLights = 0x5448474C;      // "LGHT"
    public const uint MagicTileArt = 0x54524154;     // "TART"
    public const uint MagicEquipConv = 0x56435145;   // "EQCV"

    public const string TileDataExtFile = "tiledata_ext.bin";
    public const string HueRawFile = "hues_raw.bin";
    public const string RadarColFile = "radarcol.bin";
    public const string MultisFile = "multis.bin";
    public const string LightsFile = "lights.bin";
    public const string TileArtFile = "tileart.bin";
    public const string EquipConvFile = "equipconv.bin";
}

/// <summary>Canonical 5->8 bit expansion: round(c*255/31) = (c*527 + 23) >> 6, what GPUs return for B5G5R5A1_UNORM.</summary>
public static class PackColor
{
    public static byte Expand5(int c) => (byte)((c * 527 + 23) >> 6);

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
}

/// <summary>
/// tiledata_ext.bin: landCount i32, staticCount i32, nameBytes i32, LandTileInfo[landCount], StaticTileInfo[staticCount],
/// then the UTF-8 name table. Names are referenced by (offset, length) into the table; duplicates share bytes.
/// </summary>
public static class TileDataExtFile
{
    public static void Write(string path, ReadOnlySpan<LandTileInfo> land, ReadOnlySpan<StaticTileInfo> statics, ReadOnlySpan<byte> names)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, TableConst.MagicTileDataExt);
        StructIo.Write(fs, land.Length);
        StructIo.Write(fs, statics.Length);
        StructIo.Write(fs, names.Length);
        StructIo.WriteSpan(fs, land);
        StructIo.WriteSpan(fs, statics);
        fs.Write(names);
    }

    public static (LandTileInfo[] Land, StaticTileInfo[] Statics, byte[] Names) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, TableConst.MagicTileDataExt);
        var land = new LandTileInfo[StructIo.Read<int>(fs)];
        var statics = new StaticTileInfo[StructIo.Read<int>(fs)];
        var names = new byte[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<LandTileInfo>(fs, land);
        StructIo.ReadSpan<StaticTileInfo>(fs, statics);
        fs.ReadExactly(names);
        return (land, statics, names);
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
                if (b.Length > ushort.MaxValue) throw new InvalidDataException("name too long");
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
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, magic);
        StructIo.Write(fs, entries.Length);
        StructIo.WriteSpan(fs, entries);
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

/// <summary>hues_raw.bin: count i32, HueEntry[count].</summary>
public static class HueRawFile
{
    public static void Write(string path, ReadOnlySpan<HueEntry> entries) => TableFile.Write(path, TableConst.MagicHueRaw, entries);
    public static HueEntry[] Read(string path) => TableFile.Read<HueEntry>(path, TableConst.MagicHueRaw);
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
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, TableConst.MagicMultis);
        StructIo.Write(fs, multis.Length);
        StructIo.Write(fs, components.Length);
        StructIo.WriteSpan(fs, multis);
        StructIo.WriteSpan(fs, components);
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
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, TableConst.MagicLights);
        StructIo.Write(fs, lights.Length);
        StructIo.Write(fs, data.Length);
        StructIo.WriteSpan(fs, lights);
        fs.Write(data);
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
