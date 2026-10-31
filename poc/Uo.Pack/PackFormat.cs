using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Uo.Pack;

// Binary layouts for pack format 1.0. See docs/modern-client/pack-format.md.
// All structs are blittable and read/written with MemoryMarshal — no per-element parsing.

public static class PackConst
{
    /// <summary>FileHeader.Version written by this library (format 1.0).</summary>
    public const ushort Version = 1;
    /// <summary>Oldest FileHeader.Version readers accept. Version 0 files have the same layouts (pre-1.0 packs).</summary>
    public const ushort MinReadVersion = 0;

    public static bool VersionSupported(ushort v) => v >= MinReadVersion && v <= Version;

    public const uint MagicPage = 0x4B415050;    // "PPAK"
    public const uint MagicSprites = 0x53525053; // "SPRS"
    public const uint MagicRegion = 0x47524F55;  // "UORG"
    /// <summary>Retired: the pre-1.1 compact tiledata.bin (the renderer copy). tiledata.bin is now the single full file
    /// (<see cref="TableConst.MagicTileData"/>); a reader that meets this magic reports an outdated pack.</summary>
    public const uint MagicTileDataCompactRetired = 0x44544C54; // "TLTD"
    public const uint MagicHues = 0x53455548;    // "HUES": retired hues.bin, read only by the legacy HueFile reader

    public const int StaticSpriteBase = 0x4000;
    /// <summary>tiledata.bin is a standardized format: every pack has exactly <see cref="LandTileCount"/> land entries and
    /// <see cref="StaticTileCount"/> static entries, whatever the source install. A pre-7.0.9 install has only 0x4000 statics;
    /// the converter pads the rest with empty entries, so a server-sent u16 graphic always indexes inside the table. The
    /// writer and the reader both require these counts.</summary>
    public const int LandTileCount = 0x4000;
    /// <summary>Static tiledata entries in every pack (UO graphics are u16). See <see cref="LandTileCount"/>.</summary>
    public const int StaticTileCount = 0x10000;

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
    /// <summary>Experimental (anims): one byte per texel, a palette index; 0 = transparent. The palette comes from
    /// anim/palettes.bin per frame. mip0 only; lower mips, if any, are a separate B5G5R5A1 page (mips_NNNN.bin).</summary>
    Indexed8 = 3,
}

/// <summary>FileHeader.Kind values for page and region files.</summary>
public static class FileKind
{
    public const ushort Raw = 0;
    /// <summary>Everything after the 16-byte FileHeader is one Brotli stream. v1.</summary>
    public const ushort Brotli = 1;
    /// <summary>ChunkedHeader, u32 CompressedSize[ChunkCount], then independent Brotli streams. See ChunkedBrotli.</summary>
    public const ushort ChunkedBrotli = 2;
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
    /// <summary>Retired (pack 1.1): bit 2 copied the tiledata PartialHue flag. It is no longer written and readers ignore
    /// it; the flag comes from tiledata.bin. Packs before 1.1 may still have it set.</summary>
    RetiredPartialHue = 1 << 2,
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

/// <summary>The renderer's compact land entry (12 bytes). Not stored: built at load from tiledata.bin by
/// <see cref="TileDataFile.Compact"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LandTileData
{
    public ulong Flags;
    public ushort TexId;
    public TilePackFlags PackFlags;
}

/// <summary>The renderer's compact static entry (12 bytes). Not stored: built at load from tiledata.bin by
/// <see cref="TileDataFile.Compact"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticTileData
{
    public ulong Flags;
    public byte Height;
    public byte Layer;
    public TilePackFlags PackFlags;
}

/// <summary>
/// Per-tile draw rules, stored in tiledata.bin's packFlags. The converter derives them once from ClassicUO's former
/// hard-coded rules (see "NoDraw flags" in pack-format.md); afterwards they are data: a shard admin hides or shows a tile
/// by editing the flag. Clients and the renderer read only these bits (<see cref="TileDraw"/>), never ids or names.
/// </summary>
[Flags]
public enum TilePackFlags : ushort
{
    None = 0,
    /// <summary>Never drawn: land, or a static/item/multi component/effect graphic.</summary>
    NoDraw = 1 << 0,
    /// <summary>Static graphic hidden unless the player is a gargoyle.</summary>
    NoDrawUnlessGargoyle = 1 << 1,
    /// <summary>Informational: the tiledata name starts with "nodraw" (case-insensitive). Nothing reads it; the converter
    /// folds the name rule into <see cref="NoDraw"/> for statics.</summary>
    NameNoDraw = 1 << 2,
    /// <summary>A multi item whose display graphic (the multi's first component) is this static is not drawn.</summary>
    NoDrawAsMulti = 1 << 3,
}

/// <summary>The draw decisions made from <see cref="TilePackFlags"/>. Data only: no tile ids or names.</summary>
public static class TileDraw
{
    /// <summary>A land tile (its graphic after the season swap) is drawn.</summary>
    public static bool Land(TilePackFlags f) => (f & TilePackFlags.NoDraw) == 0;

    /// <summary>A static, item, multi component or effect graphic (after the season swap) is drawn.</summary>
    public static bool Static(TilePackFlags f, bool playerIsGargoyle) =>
        (f & TilePackFlags.NoDraw) == 0 && ((f & TilePackFlags.NoDrawUnlessGargoyle) == 0 || playerIsGargoyle);

    /// <summary>A multi item with this display graphic is drawn.</summary>
    public static bool MultiItem(TilePackFlags f) => (f & TilePackFlags.NoDrawAsMulti) == 0;
}

// ---- v1 animations (anim/anim.bin, anim/animdata.bin). See "v1 animations" in docs/modern-client/pack-format-v0.md.

public static class AnimConst
{
    public const uint MagicAnim = 0x4D494E41;     // "ANIM"
    public const uint MagicAnimData = 0x54444E41; // "ANDT"
    public const uint MagicPalettes = 0x4C415041; // "APAL"
    /// <summary>Experimental Indexed8 sidecar: B5G5R5A1 page holding mips 1+ of anim page NNNN.</summary>
    public static string MipsPath(string dir, int page) => Path.Combine(dir, $"mips_{page:D4}.bin");
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
    /// <summary>Bodyconv.def mounted height offset of this entry's own conversion (informational; the client reads MountShift).</summary>
    public sbyte MountHeight;
    /// <summary>
    /// Rider shift for this body as a mount: the mount height of the art the pack draws for it (after its Body.def
    /// stand-in, if any) minus that of the default (aos) art. The client adds it to the Mounts table's per-item offset,
    /// so 0 (every default pack, and packs before this field, where it was padding) draws riders as the table says.
    /// </summary>
    public sbyte MountShift;

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
    /// <summary>The body import choices anim/ was built with (bodies.json, resolved); null in packs from before them (= the legacy rules, policy 0).</summary>
    public BodyChoicesRecord? Bodies { get; set; }
}

/// <summary>Manifest record of the body import choices (docs/modern-client/bodies.md), for diagnostics and --verify-only.</summary>
public sealed class BodyChoicesRecord
{
    /// <summary>The converter's body resolver policy (Uo.Converter.BodyChoices.CurrentPolicy); 0 never appears in a record.</summary>
    public int Policy { get; set; }
    public string Preset { get; set; } = "aos";
    public bool ForceOldAnimations { get; set; }
    public string Additions { get; set; } = "standins";
    /// <summary>"none" or "target": whether converted MUL bodies follow their target's AnimationSequence.</summary>
    public string Sequences { get; set; } = "none";
    public List<BodyRemodelRecord> Remodels { get; set; } = new();
    public List<BodyExtraRecord> Extras { get; set; } = new();
    /// <summary>"default" or the bodies.json path, and that file's SHA-256.</summary>
    public string Source { get; set; } = "default";
    public string Sha256 { get; set; } = "";
}

public sealed class BodyRemodelRecord
{
    public int Id { get; set; }
    public string Use { get; set; } = "";
}

public sealed class BodyExtraRecord
{
    public int Id { get; set; }
    public string From { get; set; } = "";
    /// <summary>Set only when bodies.json gave it.</summary>
    public int? MountHeight { get; set; }
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
        if (!PackConst.VersionSupported(h.Version))
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
        if (!PackConst.VersionSupported(h.Version))
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
