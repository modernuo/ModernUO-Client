using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Uo.Pack;

// Binary layouts for pack format v0. See docs/modern-client/pack-format-v0.md.
// All structs are blittable and read/written with MemoryMarshal — no per-element parsing.

public static class PackConst
{
    public const ushort Version = 0;

    public const uint MagicPage = 0x4B415050;    // "PPAK"
    public const uint MagicSprites = 0x53525053; // "SPRS"
    public const uint MagicRegion = 0x47524F55;  // "UORG"
    public const uint MagicTileData = 0x44544C54; // "TLTD"
    public const uint MagicHues = 0x53455548;    // "HUES"

    public const int StaticSpriteBase = 0x4000;
    public const int LandTileCount = 0x4000;

    public const int PageSize = 2048;
    public const int PageMipCount = 6;
    public const int Gutter = 2;

    public const int BlockCells = 8;
    public const int CellsPerBlock = BlockCells * BlockCells;
    public const int RegionBlocks = 64;

    public const ushort NoPage = 0xFFFF;

    public static string PagePath(string dir, int page) => Path.Combine(dir, $"page_{page:D4}.bin");
    public static string RegionPath(string facetDir, int rx, int ry) => Path.Combine(facetDir, $"r_{rx}_{ry}.bin");
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FileHeader
{
    public uint Magic;
    public ushort Version;
    public ushort Kind;
    public ulong Reserved;
}

public enum PixelFormat : ushort
{
    Rgba8 = 0,
    /// <summary>BC7 (BPTC) unorm, 16 bytes per 4x4 block, blocks row-major. v1.</summary>
    Bc7 = 1,
    /// <summary>16-bit, little-endian u16 per texel: bits 0-4 B, 5-9 G, 10-14 R, 15 A (SDL_GPU B5G5R5A1_UNORM). v1.
    /// UO's own 1555 colour layout, so mip0 is bit-exact to the client data.</summary>
    B5G5R5A1 = 2,
}

/// <summary>FileHeader.Kind values for region files.</summary>
public static class FileKind
{
    public const ushort Raw = 0;
    /// <summary>Everything after the 16-byte FileHeader is one Brotli stream. v1.</summary>
    public const ushort Brotli = 1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PageHeader
{
    public ushort Width, Height, MipCount;
    public PixelFormat Format;
    public uint DataBytes;
    public uint Reserved;
}

[Flags]
public enum SpriteFlags : ushort
{
    None = 0,
    Land = 1 << 0,
    Texmap = 1 << 1,
    PartialHue = 1 << 2,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SpriteEntry
{
    public ushort Page;
    public ushort X, Y, W, H;
    public short TrimX, TrimY;
    public ushort OrigW, OrigH;
    public SpriteFlags Flags;

    public readonly bool Present => Page != PackConst.NoPage;
    public static SpriteEntry Absent => new() { Page = PackConst.NoPage };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RegionHeader
{
    public ushort Facet, Rx, Ry, BlocksW, BlocksH, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LandCell
{
    public ushort TileId;
    public sbyte Z;
    public byte Pad;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticCell
{
    public ushort Graphic;
    public byte X, Y;
    public sbyte Z;
    public byte Pad;
    public ushort Hue;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LandTileData
{
    public ulong Flags;
    public ushort TexId;
    public ushort Pad;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticTileData
{
    public ulong Flags;
    public byte Height;
    public byte Layer;
    public ushort Pad;
}

public sealed class FacetInfo
{
    public int Index { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BlocksX { get; set; }
    public int BlocksY { get; set; }
    public int RegionBlocks { get; set; } = PackConst.RegionBlocks;
}

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class Manifest
{
    public int Version { get; set; } = PackConst.Version;
    public string SourcePath { get; set; } = "";
    public string SourceClientVersion { get; set; } = "";
    public DateTime Created { get; set; }
    public List<FacetInfo> Facets { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = new();
}

public static class StructIo
{
    public static void WriteHeader(Stream s, uint magic, ushort kind = 0)
    {
        var h = new FileHeader { Magic = magic, Version = PackConst.Version, Kind = kind };
        Write(s, in h);
    }

    public static void ReadHeader(Stream s, uint expectedMagic)
    {
        var h = Read<FileHeader>(s);
        if (h.Magic != expectedMagic)
            throw new InvalidDataException($"Bad magic 0x{h.Magic:X8}, expected 0x{expectedMagic:X8}");
        if (h.Version != PackConst.Version)
            throw new InvalidDataException($"Unsupported pack version {h.Version}");
    }

    public static void Write<T>(Stream s, in T value) where T : unmanaged
        => s.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    public static void WriteSpan<T>(Stream s, ReadOnlySpan<T> values) where T : unmanaged
        => s.Write(MemoryMarshal.AsBytes(values));

    /// <summary>Reads and validates a FileHeader, returning it (so callers can inspect Kind).</summary>
    public static FileHeader ReadHeaderFull(Stream s, uint expectedMagic)
    {
        var h = Read<FileHeader>(s);
        if (h.Magic != expectedMagic)
            throw new InvalidDataException($"Bad magic 0x{h.Magic:X8}, expected 0x{expectedMagic:X8}");
        if (h.Version != PackConst.Version)
            throw new InvalidDataException($"Unsupported pack version {h.Version}");
        return h;
    }

    public static T Read<T>(Stream s) where T : unmanaged
    {
        T value = default;
        s.ReadExactly(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }

    public static void ReadSpan<T>(Stream s, Span<T> into) where T : unmanaged
        => s.ReadExactly(MemoryMarshal.AsBytes(into));

    public static int SizeOf<T>() where T : unmanaged => Unsafe.SizeOf<T>();
}
