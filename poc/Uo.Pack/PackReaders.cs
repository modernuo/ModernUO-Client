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

    /// <summary>Bytes of one mip level in the given format (BC7: 16 bytes per 4x4 block, i.e. 1 byte/pixel).</summary>
    public static int MipBytes(int size, int mip, PixelFormat format)
    {
        int s = size >> mip;
        return format switch
        {
            PixelFormat.Rgba8 => s * s * 4,
            PixelFormat.Bc7 => ((s + 3) / 4) * ((s + 3) / 4) * 16,
            PixelFormat.B5G5R5A1 => s * s * 2,
            _ => throw new NotSupportedException(format.ToString()),
        };
    }

    public static int ChainBytes(int size, int mips, PixelFormat format)
    {
        var total = 0;
        for (int m = 0; m < mips; m++) total += MipBytes(size, m, format);
        return total;
    }

    /// <summary>Byte offset of mip level <paramref name="mip"/> inside the chain.</summary>
    public static int MipOffset(int size, int mip, PixelFormat format) => ChainBytes(size, mip, format);

    public static void Write(string path, int size, int mips, ReadOnlySpan<byte> chain)
        => Write(path, size, mips, PixelFormat.Rgba8, chain);

    public static void Write(string path, int size, int mips, PixelFormat format, ReadOnlySpan<byte> chain)
        => Write(path, size, mips, format, chain, compress: false);

    /// <summary>Brotli quality used for Kind=1 pages.</summary>
    public const int BrotliQuality = 5;

    /// <summary>
    /// Writes a page. With <paramref name="compress"/> the file is FileHeader(Kind=1) followed by one Brotli stream of
    /// everything a Kind=0 file has after its FileHeader (PageHeader + chain). DataBytes is always the uncompressed size.
    /// </summary>
    public static void Write(string path, int size, int mips, PixelFormat format, ReadOnlySpan<byte> chain, bool compress)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicPage, compress ? FileKind.Brotli : FileKind.Raw);
        var h = new PageHeader { Width = (ushort)size, Height = (ushort)size, MipCount = (ushort)mips, Format = format, DataBytes = (uint)chain.Length };
        if (!compress)
        {
            StructIo.Write(fs, in h);
            fs.Write(chain);
            return;
        }
        using var br = new System.IO.Compression.BrotliStream(fs, new System.IO.Compression.BrotliCompressionOptions { Quality = BrotliQuality });
        StructIo.Write(br, in h);
        br.Write(chain);
    }

    /// <summary>
    /// Opens a page (Kind 0 or 1) and returns a stream positioned at the start of the mip chain, plus the header.
    /// For Kind 1 the stream is a forward-only streaming Brotli decoder. Dispose it when done.
    /// </summary>
    public static Stream OpenChain(string path, out PageHeader header)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            var fh = StructIo.ReadHeaderFull(fs, PackConst.MagicPage);
            Stream s = fh.Kind switch
            {
                FileKind.Raw => fs,
                FileKind.Brotli => new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionMode.Decompress),
                _ => throw new InvalidDataException($"Unknown page kind {fh.Kind}"),
            };
            header = StructIo.Read<PageHeader>(s);
            return s;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads a page (raw or Brotli, transparently) into <paramref name="into"/>, which must be at least DataBytes long.
    /// Decompression streams straight into the caller's span with no intermediate full-size buffer. Returns the header.
    /// </summary>
    public static PageHeader Read(string path, Span<byte> into)
    {
        using var s = OpenChain(path, out var h);
        s.ReadExactly(into[..(int)h.DataBytes]);
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
    /// <summary>Brotli quality used for Kind=1 regions.</summary>
    public const int BrotliQuality = 5;

    public static void Write(string path, in RegionHeader header, ReadOnlySpan<BlockData> blocks)
        => Write(path, in header, blocks, compress: false);

    /// <summary>
    /// Writes a region. With <paramref name="compress"/> the file is FileHeader(Kind=1) followed by a Brotli stream of
    /// everything a Kind=0 file has after its FileHeader. Block offsets are always relative to the start of the
    /// logical (uncompressed) file, i.e. they include the 16-byte FileHeader.
    /// </summary>
    public static void Write(string path, in RegionHeader header, ReadOnlySpan<BlockData> blocks, bool compress)
    {
        if (!compress)
        {
            using var f = File.Create(path);
            WriteRaw(f, in header, blocks);
            return;
        }
        using var ms = new MemoryStream();
        WriteRaw(ms, in header, blocks);
        int hs = StructIo.SizeOf<FileHeader>();
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicRegion, FileKind.Brotli);
        using (var br = new System.IO.Compression.BrotliStream(fs, new System.IO.Compression.BrotliCompressionOptions { Quality = BrotliQuality }, leaveOpen: true))
            br.Write(ms.GetBuffer().AsSpan(hs, (int)ms.Length - hs));
    }

    private static void WriteRaw(Stream fs, in RegionHeader header, ReadOnlySpan<BlockData> blocks)
    {
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
    public static (RegionHeader Header, BlockData[] Blocks) Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Returns the logical (uncompressed, Kind=0 layout) bytes of a region file; Kind 0 is returned as-is.</summary>
    public static byte[] Decompress(byte[] fileBytes)
    {
        int hs = StructIo.SizeOf<FileHeader>();
        FileHeader fh;
        using (var hms = new MemoryStream(fileBytes, 0, hs, writable: false)) fh = StructIo.ReadHeaderFull(hms, PackConst.MagicRegion);
        if (fh.Kind == FileKind.Raw) return fileBytes;
        if (fh.Kind != FileKind.Brotli) throw new InvalidDataException($"Unknown region kind {fh.Kind}");
        using var outMs = new MemoryStream(fileBytes.Length * 6);
        outMs.Write(fileBytes, 0, hs); // the logical file keeps its header so block offsets stay file-relative
        using (var br = new System.IO.Compression.BrotliStream(new MemoryStream(fileBytes, hs, fileBytes.Length - hs, writable: false), System.IO.Compression.CompressionMode.Decompress))
            br.CopyTo(outMs);
        var logical = outMs.ToArray();
        logical[6] = 0; logical[7] = 0; // FileHeader.Kind = Raw: the result is a valid Kind-0 file
        return logical;
    }

    /// <summary>Parses a region from its file bytes (Kind 0, or Kind 1 = Brotli).</summary>
    public static (RegionHeader Header, BlockData[] Blocks) Parse(byte[] fileBytes)
    {
        var bytes = Decompress(fileBytes);
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

/// <summary>anim/anim.bin: counts, then AnimBodyEntry[] (index = body id), AnimDirEntry[], AnimFrameEntry[]. v1.</summary>
public static class AnimTableFile
{
    public const string FileName = "anim.bin";

    public static void Write(string path, ReadOnlySpan<AnimBodyEntry> bodies, ReadOnlySpan<AnimDirEntry> dirs, ReadOnlySpan<AnimFrameEntry> frames)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, AnimConst.MagicAnim);
        StructIo.Write(fs, bodies.Length);
        StructIo.Write(fs, dirs.Length);
        StructIo.Write(fs, frames.Length);
        StructIo.WriteSpan(fs, bodies);
        StructIo.WriteSpan(fs, dirs);
        StructIo.WriteSpan(fs, frames);
    }

    public static (AnimBodyEntry[] Bodies, AnimDirEntry[] Dirs, AnimFrameEntry[] Frames) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, AnimConst.MagicAnim);
        var bodies = new AnimBodyEntry[StructIo.Read<int>(fs)];
        var dirs = new AnimDirEntry[StructIo.Read<int>(fs)];
        var frames = new AnimFrameEntry[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<AnimBodyEntry>(fs, bodies);
        StructIo.ReadSpan<AnimDirEntry>(fs, dirs);
        StructIo.ReadSpan<AnimFrameEntry>(fs, frames);
        return (bodies, dirs, frames);
    }

    /// <summary>Frames of (body, action, UO direction 0..7); empty when the body has no such action. Does not follow AliasBody.</summary>
    public static ReadOnlySpan<AnimFrameEntry> Frames(AnimBodyEntry[] bodies, AnimDirEntry[] dirs, AnimFrameEntry[] frames, int body, int action, int dir, out AnimDirEntry entry)
    {
        entry = default;
        if ((uint)body >= (uint)bodies.Length || action < 0 || action >= bodies[body].ActionCount) return default;
        entry = dirs[bodies[body].FirstDir + action * AnimConst.Directions + (dir & 7)];
        return frames.AsSpan((int)entry.FirstFrame, entry.FrameCount);
    }
}

/// <summary>anim/animdata.bin: count, then AnimDataEntry[] sorted by graphic (only graphics with frames). v1.</summary>
public static class AnimDataFile
{
    public const string FileName = "animdata.bin";

    public static void Write(string path, ReadOnlySpan<AnimDataEntry> entries)
    {
        using var fs = File.Create(path);
        StructIo.WriteHeader(fs, AnimConst.MagicAnimData);
        StructIo.Write(fs, entries.Length);
        StructIo.WriteSpan(fs, entries);
    }

    public static AnimDataEntry[] Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, AnimConst.MagicAnimData);
        var entries = new AnimDataEntry[StructIo.Read<int>(fs)];
        StructIo.ReadSpan<AnimDataEntry>(fs, entries);
        return entries;
    }
}

public static class ManifestFileIo
{

    public static void Write(string path, Manifest m) => File.WriteAllText(path, JsonSerializer.Serialize(m, PackJsonContext.Default.Manifest));
    public static Manifest Read(string path) => JsonSerializer.Deserialize(File.ReadAllText(path), PackJsonContext.Default.Manifest)!;
}

/// <summary>map/&lt;facet&gt;/facet.json.</summary>
public static class FacetFileIo
{
    public const string FileName = "facet.json";

    public static void Write(string facetDir, FacetInfo f) => File.WriteAllText(Path.Combine(facetDir, FileName), JsonSerializer.Serialize(f, PackJsonContext.Default.FacetInfo));
    public static FacetInfo Read(string facetDir) => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(facetDir, FileName)), PackJsonContext.Default.FacetInfo)!;
}

// camelCase to match the spec's field names; case-insensitive on read.
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Manifest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(FacetInfo))]
internal partial class PackJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
