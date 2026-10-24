using System.Text.Json;

namespace Uo.Pack;

/// <summary>Sprite tables (art/sprites.bin, tex/texmaps.bin): a count followed by SpriteEntry[].</summary>
public static class SpriteTable
{
    public static void Write(string path, ReadOnlySpan<SpriteEntry> entries)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicSprites);
        StructIo.Write(fs, entries.Length);
        StructIo.WriteSpan(fs, entries);
    }

    public static SpriteEntry[] Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, PackConst.MagicSprites);
        var count = StructIo.Read<int>(fs);
        var entries = new SpriteEntry[count];
        StructIo.ReadSpan<SpriteEntry>(fs, entries);
        return entries;
    }
}

/// <summary>Atlas pages: PageHeader followed by the RGBA8 mip chain, mip0 first.</summary>
public static class AtlasPage
{
    public static int MipBytes(int size, int mip) => (size >> mip) * (size >> mip) * 4;

    public static int ChainBytes(int size, int mips)
    {
        var total = 0;
        for (int m = 0; m < mips; m++) total += MipBytes(size, m);
        return total;
    }

    public static void Write(string path, int size, int mips, ReadOnlySpan<byte> chain)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicPage);
        var h = new PageHeader { Width = (ushort)size, Height = (ushort)size, MipCount = (ushort)mips, Format = PixelFormat.Rgba8, DataBytes = (uint)chain.Length };
        StructIo.Write(fs, in h);
        fs.Write(chain);
    }

    /// <summary>Reads a page into <paramref name="into"/>, which must be at least DataBytes long. Returns the header.</summary>
    public static PageHeader Read(string path, Span<byte> into)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, PackConst.MagicPage);
        var h = StructIo.Read<PageHeader>(fs);
        fs.ReadExactly(into[..(int)h.DataBytes]);
        return h;
    }
}

/// <summary>A decoded map block: 64 land cells plus its statics.</summary>
public readonly struct BlockData
{
    public readonly LandCell[] Land;
    public readonly StaticCell[] Statics;

    public BlockData(LandCell[] land, StaticCell[] statics) { Land = land; Statics = statics; }
    public bool IsEmpty => Land == null;
}

/// <summary>Map regions: RegionHeader, block offset table, then block records.</summary>
public static class RegionFile
{
    public static void Write(string path, in RegionHeader header, ReadOnlySpan<BlockData> blocks)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicRegion);
        StructIo.Write(fs, in header);
        var n = header.BlocksW * header.BlocksH;
        var tablePos = fs.Position;
        var offsets = new uint[n];
        StructIo.WriteSpan<uint>(fs, offsets); // placeholder
        for (int i = 0; i < n; i++)
        {
            var b = blocks[i];
            if (b.IsEmpty) continue;
            offsets[i] = (uint)fs.Position;
            StructIo.WriteSpan<LandCell>(fs, b.Land);
            StructIo.Write(fs, (ushort)b.Statics.Length);
            StructIo.Write(fs, (ushort)0);
            StructIo.WriteSpan<StaticCell>(fs, b.Statics);
        }
        fs.Position = tablePos;
        StructIo.WriteSpan<uint>(fs, offsets);
    }

    /// <summary>Reads a whole region. Blocks are returned row-major (by * BlocksW + bx).</summary>
    public static (RegionHeader Header, BlockData[] Blocks) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes, writable: false);
        StructIo.ReadHeader(ms, PackConst.MagicRegion);
        var header = StructIo.Read<RegionHeader>(ms);
        var n = header.BlocksW * header.BlocksH;
        var offsets = new uint[n];
        StructIo.ReadSpan<uint>(ms, offsets);
        var blocks = new BlockData[n];
        for (int i = 0; i < n; i++)
        {
            if (offsets[i] == 0) continue;
            ms.Position = offsets[i];
            var land = new LandCell[PackConst.CellsPerBlock];
            StructIo.ReadSpan<LandCell>(ms, land);
            var count = StructIo.Read<ushort>(ms);
            _ = StructIo.Read<ushort>(ms);
            var statics = new StaticCell[count];
            StructIo.ReadSpan<StaticCell>(ms, statics);
            blocks[i] = new BlockData(land, statics);
        }
        return (header, blocks);
    }
}

public static class TileDataFile
{
    public static void Write(string path, ReadOnlySpan<LandTileData> land, ReadOnlySpan<StaticTileData> statics)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicTileData);
        StructIo.Write(fs, land.Length);
        StructIo.Write(fs, statics.Length);
        StructIo.WriteSpan(fs, land);
        StructIo.WriteSpan(fs, statics);
    }

    public static (LandTileData[] Land, StaticTileData[] Statics) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, PackConst.MagicTileData);
        var land = new LandTileData[StructIo.Read<int>(fs)];
        var statics = new StaticTileData[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<LandTileData>(fs, land);
        StructIo.ReadSpan<StaticTileData>(fs, statics);
        return (land, statics);
    }
}

/// <summary>Hue LUT: count, then count rows of 32 RGBA8 shades.</summary>
public static class HueFile
{
    public const int Shades = 32;

    public static void Write(string path, int count, ReadOnlySpan<uint> rgba)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicHues);
        StructIo.Write(fs, count);
        StructIo.WriteSpan(fs, rgba);
    }

    public static (int Count, uint[] Rgba) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, PackConst.MagicHues);
        var count = StructIo.Read<int>(fs);
        var data = new uint[count * Shades];
        StructIo.ReadSpan<uint>(fs, data);
        return (count, data);
    }
}

public static class ManifestFileIo
{

    public static void Write(string path, Manifest m) => File.WriteAllText(path, JsonSerializer.Serialize(m, PackJsonContext.Default.Manifest));
    public static Manifest Read(string path) => JsonSerializer.Deserialize(File.ReadAllText(path), PackJsonContext.Default.Manifest)!;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Manifest))]
internal partial class PackJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
