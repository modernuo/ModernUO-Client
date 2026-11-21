using System.Text.Json;
using Uo.Buffers;

namespace Uo.Pack;

/// <summary>Sprite tables (art/sprites.bin, tex/texmaps.bin): a count followed by SpriteEntry[].</summary>
public static class SpriteTable
{
    public static void Write(string path, ReadOnlySpan<SpriteEntry> entries)
    {
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicSprites);
        StructIo.Write(fs, entries.Length);
        StructIo.WriteSpan(fs, entries);
        fs.Commit();
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
            PixelFormat.Indexed8 => s * s,
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

    /// <summary>
    /// Writes a page. With <paramref name="compress"/> the file is chunked zstd (<see cref="FileKind.ChunkedZstd"/>,
    /// 1 MiB chunks) of everything a Kind=0 file has after its FileHeader (PageHeader + chain); else Kind 0.
    /// DataBytes is always the uncompressed size.
    /// </summary>
    public static void Write(string path, int size, int mips, PixelFormat format, ReadOnlySpan<byte> chain, bool compress)
        => Write(path, size, mips, format, chain, compress ? FileKind.ChunkedZstd : FileKind.Raw);

    /// <summary>Writes a page as Kind 0 (raw) or chunked zstd (<paramref name="chunkBytes"/> per chunk).</summary>
    public static void Write(string path, int size, int mips, PixelFormat format, ReadOnlySpan<byte> chain, ushort kind, int chunkBytes = ChunkedFile.DefaultChunkBytes)
    {
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicPage, kind);
        var h = new PageHeader { Width = (ushort)size, Height = (ushort)size, MipCount = (ushort)mips, Format = format, DataBytes = (uint)chain.Length };
        switch (kind)
        {
            case FileKind.Raw:
                StructIo.Write(fs, in h);
                fs.Write(chain);
                break;
            case FileKind.ChunkedZstd:
            {
                int hs = StructIo.SizeOf<PageHeader>();
                var logical = BufferPool.Rent<byte>(hs + chain.Length);
                try
                {
                    System.Runtime.InteropServices.MemoryMarshal.Write(logical, in h);
                    chain.CopyTo(logical.AsSpan(hs));
                    ChunkedFile.Write(fs, logical.AsSpan(0, hs + chain.Length), PackCodec.OfFileKind(kind, path), chunkBytes);
                }
                finally { BufferPool.Return(logical); }
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        fs.Commit();
    }

    /// <summary>
    /// Reads only a page's <see cref="PageHeader"/>. A chunked page decodes its chunk 0 (at most one chunk, into a pooled
    /// buffer): a zstd frame decodes in one pass only into a destination that holds all of it.
    /// </summary>
    public static PageHeader ReadHeader(string path)
    {
        using var fh = File.OpenHandle(path);
        int fhs = StructIo.SizeOf<FileHeader>();
        Span<byte> head = stackalloc byte[fhs + ChunkedFile.HeaderBytes];
        int got = RandomAccess.Read(fh, head, 0);
        if (got < fhs) throw new InvalidDataException($"'{path}' is too short for a pack page");
        var fileHeader = System.Runtime.InteropServices.MemoryMarshal.Read<FileHeader>(head);
        if (fileHeader.Magic != PackConst.MagicPage) throw new InvalidDataException($"Bad magic 0x{fileHeader.Magic:X8}, expected 0x{PackConst.MagicPage:X8}");
        if (!PackConst.VersionSupported(fileHeader.Version)) throw new InvalidDataException($"Unsupported pack version {fileHeader.Version}");

        PageHeader header = default;
        var dst = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<PageHeader>(ref header));
        ushort codec = PackCodec.OfFileKind(fileHeader.Kind, path);
        if (codec == PackCodec.Raw)
        {
            if (RandomAccess.Read(fh, dst, fhs) != dst.Length) throw new EndOfStreamException();
            return header;
        }
        if (got < head.Length) throw new EndOfStreamException();
        var ch = System.Runtime.InteropServices.MemoryMarshal.Read<ChunkedHeader>(head.Slice(fhs));
        ChunkedFile.Check(ch);
        if (ch.ChunkCount == 0 || ch.LogicalBytes < (ulong)dst.Length) throw new EndOfStreamException($"'{path}': page header truncated");
        Span<byte> size0 = stackalloc byte[4];
        long table = fhs + ChunkedFile.HeaderBytes;
        if (RandomAccess.Read(fh, size0, table) != 4) throw new EndOfStreamException();
        int stored = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(size0);
        int len = ch.ChunkLength(0);
        if (stored < 0 || stored > PackCodec.MaxStoredBytes(codec, len)) throw new InvalidDataException($"'{path}': corrupt page");
        var comp = BufferPool.Rent<byte>(stored);
        var chunk = BufferPool.Rent<byte>(len);
        try
        {
            if (RandomAccess.Read(fh, comp.AsSpan(0, stored), table + 4L * ch.ChunkCount) != stored) throw new EndOfStreamException();
            PackCodec.ForThread(codec, path).Decode(comp.AsSpan(0, stored), chunk.AsSpan(0, len));
            chunk.AsSpan(0, dst.Length).CopyTo(dst);
        }
        finally
        {
            BufferPool.Return(comp);
            BufferPool.Return(chunk);
        }
        return header;
    }

    /// <summary>
    /// Opens a page (raw or chunked) and returns a stream positioned at the start of the mip chain, plus the header.
    /// For a chunked page the stream is a forward-only decoder. Dispose it when done.
    /// </summary>
    public static Stream OpenChain(string path, out PageHeader header)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            var fh = StructIo.ReadHeaderFull(fs, PackConst.MagicPage);
            ushort codec = PackCodec.OfFileKind(fh.Kind, path);
            Stream s = codec == PackCodec.Raw ? fs : new ChunkedStream(fs, codec);
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
    /// Reads a page (raw or compressed, transparently) into <paramref name="into"/>, which must be at least DataBytes long.
    /// Returns the header.
    /// </summary>
    public static PageHeader Read(string path, Span<byte> into)
    {
        using var s = OpenChain(path, out var h);
        s.ReadExactly(into[..(int)h.DataBytes]);
        return h;
    }

    /// <summary>
    /// As <see cref="Read(string, Span{byte})"/>, but a chunked page is decoded by <paramref name="decoder"/>'s workers
    /// straight into <paramref name="into"/> (chunk 0 goes through a worker scratch buffer to split off the PageHeader).
    /// </summary>
    public static PageHeader Read(string path, Span<byte> into, ChunkedDecoder decoder)
    {
        using var fh = File.OpenHandle(path);
        FileHeader f = default;
        RandomAccess.Read(fh, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<FileHeader>(ref f)), 0);
        if (f.Magic != PackConst.MagicPage) throw new InvalidDataException($"Bad magic 0x{f.Magic:X8}");
        if (!PackConst.VersionSupported(f.Version)) throw new InvalidDataException($"Unsupported pack version {f.Version}");
        ushort codec = PackCodec.OfFileKind(f.Kind, path);
        if (codec == PackCodec.Raw)
        {
            fh.Dispose();
            return Read(path, into);
        }
        PageHeader h = default;
        int hs = StructIo.SizeOf<PageHeader>();
        decoder.Decode(fh, StructIo.SizeOf<FileHeader>(), codec, into, hs, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<PageHeader>(ref h)));
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
        => Write(path, in header, blocks, compress: false);

    /// <summary>
    /// Writes a region. With <paramref name="compress"/> the file is chunked zstd (<see cref="FileKind.ChunkedZstd"/>) of
    /// everything a Kind=0 file has after its FileHeader, as one chunk: a region decodes on one worker anyway (regions
    /// are prefetched), and one frame compresses better than 1 MiB pieces. Block offsets are always relative to the
    /// start of the logical (uncompressed) file, i.e. they include the 16-byte FileHeader.
    /// </summary>
    public static void Write(string path, in RegionHeader header, ReadOnlySpan<BlockData> blocks, bool compress)
    {
        if (!compress)
        {
            using var f = ReplacingFile.Create(path);
            WriteRaw(f, in header, blocks);
            f.Commit();
            return;
        }
        using var ms = new MemoryStream();
        WriteRaw(ms, in header, blocks);
        int hs = StructIo.SizeOf<FileHeader>();
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, PackConst.MagicRegion, FileKind.ChunkedZstd);
        var logical = ms.GetBuffer().AsSpan(hs, (int)ms.Length - hs);
        ChunkedFile.Write(fs, logical, PackCodec.Zstd, Math.Max(1, logical.Length));
        fs.Commit();
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
        if (ReadKind(fileBytes) == FileKind.Raw) return fileBytes;
        byte[] dest = null;
        int n = DecompressInto(fileBytes, ref dest);
        if (dest.Length != n) Array.Resize(ref dest, n);
        return dest;
    }

    static ushort ReadKind(ReadOnlySpan<byte> fileBytes)
    {
        int hs = StructIo.SizeOf<FileHeader>();
        if (fileBytes.Length < hs) throw new InvalidDataException("region file too short");
        var fh = System.Runtime.InteropServices.MemoryMarshal.Read<FileHeader>(fileBytes);
        if (fh.Magic != PackConst.MagicRegion || !PackConst.VersionSupported(fh.Version)) throw new InvalidDataException("not a supported region file");
        return fh.Kind;
    }

    /// <summary>
    /// Writes the logical (Kind=0 layout, header included) bytes of a region file into <paramref name="dest"/> and returns
    /// their length. <paramref name="dest"/> is reused when big enough, else replaced by a larger array (null = allocate).
    /// With <paramref name="pooled"/>, a larger array is rented with BufferPool.RentShared (LargeUse.Region) and a replaced
    /// one returned with ReturnShared (dest must then be null or such a rent), so the caller returns the result with
    /// ReturnShared and LargeUse.Region.
    /// </summary>
    public static int DecompressInto(ReadOnlySpan<byte> fileBytes, ref byte[] dest, bool pooled = false)
    {
        int hs = StructIo.SizeOf<FileHeader>();
        ushort kind = ReadKind(fileBytes);
        int total;
        if (kind == FileKind.Raw)
        {
            Ensure(ref dest, fileBytes.Length, 0, pooled);
            fileBytes.CopyTo(dest);
            return fileBytes.Length;
        }
        // chunked: the logical file keeps its header so block offsets stay file-relative
        ushort codec = PackCodec.OfFileKind(kind, "region");
        var body = fileBytes.Slice(hs);
        var ch = ChunkedFile.ReadHeader(body);
        total = checked(hs + (int)ch.LogicalBytes);
        Ensure(ref dest, total, 0, pooled);
        fileBytes.Slice(0, hs).CopyTo(dest);
        ChunkedFile.Decode(body, PackCodec.ForThread(codec), dest.AsSpan(hs, total - hs));
        dest[6] = 0; dest[7] = 0; // FileHeader.Kind = Raw: the result is a valid Kind-0 file
        return total;
    }

    static void Ensure(ref byte[] buffer, int size, int keep, bool pooled)
    {
        if (buffer != null && buffer.Length >= size) return;
        var bigger = pooled ? BufferPool.RentShared<byte>(size, LargeUse.Region) : new byte[size];
        if (buffer != null && keep > 0) buffer.AsSpan(0, keep).CopyTo(bigger);
        if (pooled && buffer != null) BufferPool.ReturnShared(buffer, LargeUse.Region);
        buffer = bigger;
    }

    /// <summary>Parses a region from its file bytes (raw or chunked).</summary>
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

/// <summary>Retired hues.bin: count, then count rows of 32 RGBA8 shades. Packs made before the one-source-of-truth change
/// (e.g. classic-1.0) still carry it, but nothing loads it: the LUT is built from hues_raw.bin by <see cref="HueLut"/>.
/// Only kept so tests and tools can compare an old pack's file with the built LUT.</summary>
public static class HueFile
{
    public const int Shades = HueLut.Shades;
    public const string LegacyFileName = "hues.bin";

    public static (int Count, uint[] Rgba) Read(string path)
    {
        using var fs = File.OpenRead(path);
        StructIo.ReadHeader(fs, PackConst.MagicHues);
        var count = StructIo.Read<int>(fs);
        if (count < 0 || (long)count * Shades * 4 > fs.Length - fs.Position)
            throw new InvalidDataException($"{LegacyFileName}: bad count {count}");
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
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, AnimConst.MagicAnim);
        StructIo.Write(fs, bodies.Length);
        StructIo.Write(fs, dirs.Length);
        StructIo.Write(fs, frames.Length);
        StructIo.WriteSpan(fs, bodies);
        StructIo.WriteSpan(fs, dirs);
        StructIo.WriteSpan(fs, frames);
        fs.Commit();
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
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, AnimConst.MagicAnimData);
        StructIo.Write(fs, entries.Length);
        StructIo.WriteSpan(fs, entries);
        fs.Commit();
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

/// <summary>
/// anim/palettes.bin (experimental, Indexed8 anim pages): paletteCount i32, frameCount i32, u32 FramePalette[frameCount]
/// (palette id per AnimFrameEntry, uint.MaxValue for absent frames), then u16 Palettes[paletteCount * 256] as B5G5R5A1.
/// Entry 0 of every palette is 0 (transparent).
/// </summary>
public static class AnimPaletteFile
{
    public const string FileName = "palettes.bin";
    public const int Entries = 256;

    public static void Write(string path, ReadOnlySpan<uint> framePalette, ReadOnlySpan<ushort> palettes)
    {
        using var fs = ReplacingFile.Create(path);
        StructIo.WriteHeader(fs, AnimConst.MagicPalettes);
        StructIo.Write(fs, palettes.Length / Entries);
        StructIo.Write(fs, framePalette.Length);
        StructIo.WriteSpan(fs, framePalette);
        StructIo.WriteSpan(fs, palettes);
        fs.Commit();
    }

    /// <summary>Largest palette payload a reader accepts (bytes): 64k palettes, 32 MiB. The shipped pack has 37.9k
    /// (18.5 MiB). Packs are untrusted input; the GPU upload sizes a buffer from this.</summary>
    public const long MaxPaletteBytes = 32L << 20;
    public const int MaxPalettes = (int)(MaxPaletteBytes / (Entries * 2));
    /// <summary>Largest frame table a reader accepts (entries).</summary>
    public const int MaxFrames = 1 << 24;

    /// <summary>Where the parts of a validated palettes.bin are: counts, where the frame table and the palette data
    /// start (bytes from the file start), and the palette data's size.</summary>
    public readonly record struct Layout(int PaletteCount, int FrameCount, long FrameOffset, long PaletteOffset, long PaletteBytes);

    /// <summary>Reads the header and counts and checks them against the stream's length before anything is sized from
    /// them: non-negative counts within <see cref="MaxPalettes"/> / <see cref="MaxFrames"/>, and the frame table plus
    /// the whole palette payload inside the file. Leaves the stream at the frame table.</summary>
    public static Layout ReadLayout(Stream fs)
    {
        StructIo.ReadHeader(fs, AnimConst.MagicPalettes);
        int pc = StructIo.Read<int>(fs);
        int frames = StructIo.Read<int>(fs);
        if (pc < 0 || pc > MaxPalettes)
            throw new InvalidDataException($"{FileName}: palette count {pc} outside 0..{MaxPalettes}");
        if (frames < 0 || frames > MaxFrames)
            throw new InvalidDataException($"{FileName}: frame count {frames} outside 0..{MaxFrames}");
        long frameOffset = fs.Position;
        long palOffset = checked(frameOffset + (long)frames * sizeof(uint));
        long palBytes = checked((long)pc * Entries * sizeof(ushort));
        long end = checked(palOffset + palBytes);
        if (end > fs.Length)
            throw new InvalidDataException($"{FileName}: {frames} frames and {pc} palettes need {end} bytes, the file has {fs.Length}");
        return new Layout(pc, frames, frameOffset, palOffset, palBytes);
    }

    /// <summary>Checks every frame's palette id: below <paramref name="paletteCount"/>, or uint.MaxValue (absent frame).</summary>
    public static void ValidateFramePalette(ReadOnlySpan<uint> framePalette, int paletteCount)
    {
        for (int i = 0; i < framePalette.Length; i++)
        {
            uint id = framePalette[i];
            if (id != uint.MaxValue && id >= (uint)paletteCount)
                throw new InvalidDataException($"{FileName}: frame {i} names palette {id}, the file has {paletteCount}");
        }
    }

    public static (uint[] FramePalette, ushort[] Palettes) Read(string path)
    {
        using var fs = File.OpenRead(path);
        var l = ReadLayout(fs);
        var fp = new uint[l.FrameCount];
        StructIo.ReadSpan<uint>(fs, fp);
        ValidateFramePalette(fp, l.PaletteCount);
        var pal = new ushort[l.PaletteCount * Entries];
        StructIo.ReadSpan<ushort>(fs, pal);
        return (fp, pal);
    }

    /// <summary>The frame table only, plus where the palette data starts (<paramref name="paletteOffset"/>, bytes from
    /// the file start) and how many palettes it holds: for readers that stream the palettes elsewhere (the GPU upload)
    /// instead of holding a CPU copy. Validated (<see cref="ReadLayout(Stream)"/>, <see cref="ValidateFramePalette"/>).</summary>
    public static uint[] ReadFramePalette(string path, out int paletteCount, out long paletteOffset)
    {
        using var fs = File.OpenRead(path);
        var l = ReadLayout(fs);
        var fp = new uint[l.FrameCount];
        StructIo.ReadSpan<uint>(fs, fp);
        ValidateFramePalette(fp, l.PaletteCount);
        paletteCount = l.PaletteCount;
        paletteOffset = l.PaletteOffset;
        return fp;
    }

    /// <summary>The validated layout, without reading the frame table (the caller validates a shared one with
    /// <see cref="ValidateFramePalette"/>).</summary>
    public static Layout ReadLayout(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadLayout(fs);
    }
}

public static class ManifestFileIo
{

    public static void Write(string path, Manifest m) => ReplacingFile.WriteAllText(path, JsonSerializer.Serialize(m, PackJsonContext.Default.Manifest));
    public static Manifest Read(string path) => JsonSerializer.Deserialize(File.ReadAllText(path), PackJsonContext.Default.Manifest)!;
}

/// <summary>map/&lt;facet&gt;/facet.json.</summary>
public static class FacetFileIo
{
    public const string FileName = "facet.json";

    public static void Write(string facetDir, FacetInfo f) => ReplacingFile.WriteAllText(Path.Combine(facetDir, FileName), JsonSerializer.Serialize(f, PackJsonContext.Default.FacetInfo));
    public static FacetInfo Read(string facetDir) => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(facetDir, FileName)), PackJsonContext.Default.FacetInfo)!;
}

// camelCase to match the spec's field names; case-insensitive on read.
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Manifest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(FacetInfo))]
internal partial class PackJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
