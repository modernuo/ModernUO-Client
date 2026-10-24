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
    public TilePackFlags PackFlags;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticTileData
{
    public ulong Flags;
    public byte Height;
    public byte Layer;
    public TilePackFlags PackFlags;
}

/// <summary>Converter-derived tile flags (v1), stored in what was the pad u16 of LandTileData/StaticTileData. 0 in v0 packs.</summary>
[Flags]
public enum TilePackFlags : ushort
{
    None = 0,
    /// <summary>ClassicUO never draws this tile (Land.AllowedToDraw / GameObject.CanBeDrawn, static-data part).</summary>
    NoDraw = 1 << 0,
    /// <summary>Static hidden unless the player is a gargoyle (NoDiagonal + Animation in CanBeDrawn).</summary>
    NoDrawUnlessGargoyle = 1 << 1,
    /// <summary>The tiledata name starts with "nodraw" (case-insensitive). Informational for land: ClassicUO hides land by id only.</summary>
    NameNoDraw = 1 << 2,
}

// ---- v1 animations (anim/anim.bin, anim/animdata.bin). See "v1 animations" in docs/modern-client/pack-format-v0.md.

public static class AnimConst
{
    public const uint MagicAnim = 0x4D494E41;     // "ANIM"
    public const uint MagicAnimData = 0x54444E41; // "ANDT"
    /// <summary>Direction records per action: UO directions 0..7 (N, NE, E, SE, S, SW, W, NW).</summary>
    public const int Directions = 8;
    /// <summary>Directions stored in the client data; the other three are drawn mirrored.</summary>
    public const int StoredDirections = 5;
    public const int MaxActions = 80;
    public const ushort NoBody = 0xFFFF;

    /// <summary>ClassicUO Animations.GetAnimDirection: UO direction -> stored direction, mirrored.</summary>
    public static (byte Stored, bool Mirrored) MapDirection(int dir) => (dir & 7) switch
    {
        0 => (3, true), 1 => (2, true), 2 => (1, true), 3 => (0, false),
        4 => (1, false), 5 => (2, false), 6 => (3, false), _ => (4, false),
    };
}

/// <summary>ClassicUO AnimationGroupsType.</summary>
public enum AnimType : byte
{
    Monster = 0,
    SeaMonster = 1,
    Animal = 2,
    Human = 3,
    Equipment = 4,
    Unknown = 5,
}

/// <summary>Where a body's frames came from: bit 7 = AnimationFrame&lt;n+1&gt;.uop, else anim&lt;n+1&gt;.mul (n = low 7 bits).</summary>
public static class AnimSource
{
    public const byte Uop = 0x80;
    public const byte None = 0xFF;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AnimBodyEntry
{
    /// <summary>Dir record of (action 0, direction 0); (action a, direction d) is at FirstDir + a * 8 + d.</summary>
    public uint FirstDir;
    /// <summary>0 = the body has no frames of its own.</summary>
    public ushort ActionCount;
    public AnimType Type;
    public byte Source;
    /// <summary>mobtypes.txt flags (ClassicUO AnimationFlags); bit 31 set when the body is listed.</summary>
    public uint Flags;
    /// <summary>Body id inside the source file (the Bodyconv.def target), else the body itself.</summary>
    public ushort SourceBody;
    /// <summary>Body.def replacement, already chained (0xFFFF = none). Draw that body's entry instead, with AliasHue.</summary>
    public ushort AliasBody;
    public ushort AliasHue;
    /// <summary>Corpse.def replacement for corpses (0xFFFF = none), with CorpseHue.</summary>
    public ushort CorpseBody;
    public ushort CorpseHue;
    /// <summary>Bodyconv.def mounted height offset.</summary>
    public sbyte MountHeight;
    public byte Pad;

    public readonly bool HasFrames => ActionCount != 0;
}

[Flags]
public enum AnimDirFlags : byte
{
    None = 0,
    /// <summary>Draw the frames flipped horizontally (they are the stored direction's frames).</summary>
    Mirrored = 1 << 0,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AnimDirEntry
{
    public uint FirstFrame;
    /// <summary>Frames in this direction (absent frames included). 0 = no animation for this action.</summary>
    public ushort FrameCount;
    /// <summary>Stored client direction 0..4 the frames come from.</summary>
    public byte StoredDir;
    public AnimDirFlags Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AnimFrameEntry
{
    /// <summary>Atlas rect in anim/page_NNNN.bin. OrigW/OrigH = the frame's size, Trim = transparent border removed.</summary>
    public SpriteEntry Sprite;
    /// <summary>The client frame's CenterX/CenterY (anchor, relative to the untrimmed frame).</summary>
    public short CenterX, CenterY;
}

[System.Runtime.CompilerServices.InlineArray(64)]
public struct AnimDataFrames
{
    private sbyte _e;
}

/// <summary>animdata.mul record for one static graphic (animated art).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AnimDataEntry
{
    public ushort Graphic;
    public byte FrameCount;
    public byte FrameInterval;
    public byte FrameStart;
    public byte Unknown;
    /// <summary>Graphic offsets per frame (graphic + Frames[i]).</summary>
    public AnimDataFrames Frames;
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
