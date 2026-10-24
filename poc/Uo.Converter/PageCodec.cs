using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Uo.Pack;
using BcPixelFormat = BCnEncoder.Encoder.PixelFormat;
using PackPixelFormat = Uo.Pack.PixelFormat;

namespace Uo.Converter;

/// <summary>Global output options (set once by Program).</summary>
public static class ConvertOptions
{
    public static PackPixelFormat Format = PackPixelFormat.B5G5R5A1;
    public static CompressionQuality Bc7Quality = CompressionQuality.Fast;
    /// <summary>Regions as Kind 1 (ship profile). --raw-regions writes Kind 0.</summary>
    public static bool CompressRegions = true;
    public static string? DebugDir;
    public static bool Bc7FillTransparent = true;
    /// <summary>With Fast quality, blocks below this PSNR are re-encoded at Balanced (0 = off). --bc7-refine-db.</summary>
    public static double Bc7RefineDb = 28;
    /// <summary>Cap on worker threads for every parallel loop (--threads). Default: half the logical cores.</summary>
    public static int Threads = Math.Max(1, Environment.ProcessorCount / 2);
    public static ParallelOptions Par => new() { MaxDegreeOfParallelism = Threads };
    /// <summary>--pages a-b: only encode/write/verify atlas pages in this range (packing is still global).</summary>
    public static (int Lo, int Hi) Pages = (0, int.MaxValue);
    public static bool PageSelected(int p) => p >= Pages.Lo && p <= Pages.Hi;
    public static bool PartialPages => Pages != (0, int.MaxValue);
    /// <summary>--regions n: only the first n regions of each facet (debugging).</summary>
    public static int MaxRegions = int.MaxValue;
    /// <summary>--facets 0,2: only these facets (null = all).</summary>
    public static HashSet<int>? Facets;
    /// <summary>Packed rect alignment (incl. gutter). 4 for BC formats so no 4x4 block straddles two sprites at mip0.</summary>
    public static int Align => Format == PackPixelFormat.Bc7 ? 4 : 1;
    /// <summary>Measure-only experiment: Brotli(q5) size of every written page.</summary>
    public static bool MeasurePageBrotli = false;
    /// <summary>--compress-pages: write pages as Kind=1 (Brotli) instead of the default Kind=2.</summary>
    public static bool CompressPages;
    /// <summary>--compress-pages=chunked: write pages as Kind=2 (chunked Brotli, --chunk-bytes per chunk).</summary>
    public static bool ChunkedPages = true;
    public static int ChunkBytes = ChunkedBrotli.DefaultChunkBytes;
    public static ushort PageKind => ChunkedPages ? FileKind.ChunkedBrotli : CompressPages ? FileKind.Brotli : FileKind.Raw;
    public static bool AnyPageCompression => ChunkedPages || CompressPages;
    /// <summary>--page-size: art and tex page side. --anim-page-size: anim page side (defaults to --page-size).</summary>
    public static int PageSize = PackConst.PageSize;
    public static int? AnimPageSizeOverride = 1024;
    public static int AnimPageSize => AnimPageSizeOverride ?? PageSize;
    /// <summary>Mip levels down to 64x64, as for the 2048 default (6 levels).</summary>
    public static int MipsFor(int size) => Math.Max(1, System.Numerics.BitOperations.Log2((uint)size) - 5);
    /// <summary>Indexed8 anim pages (ship profile; see AnimPaletteFile). --anim-format page uses --format instead.</summary>
    public static bool AnimIndexed = true;
    /// <summary>--indexed-mips b16|none: with Indexed8, also write mips 1+ as a B5G5R5A1 sidecar page (mips_NNNN.bin).</summary>
    public static bool IndexedMips;
}

/// <summary>Page pixel codec: RGBA8 chain -> BC7 chain, and decoding any page mip back to RGBA8.</summary>
public static class PageCodec
{
    private static BcEncoder NewEncoder() => NewEncoder(ConvertOptions.Bc7Quality);

    private static BcEncoder NewEncoder(CompressionQuality quality)
    {
        var enc = new BcEncoder(CompressionFormat.Bc7);
        enc.OutputOptions.GenerateMipMaps = false;
        enc.OutputOptions.Quality = quality;
        enc.Options.IsParallel = false;
        return enc;
    }

    private static ThreadLocal<BcEncoder> Encoders = new(NewEncoder);
    private static readonly ThreadLocal<BcEncoder> Refiners = new(() => NewEncoder(CompressionQuality.Balanced));
    private static readonly ThreadLocal<BcDecoder> BlockDecoders = new(() => new BcDecoder());
    private static long _refined, _encoded;
    public static (long Encoded, long Refined) RefineStats => (_encoded, _refined);

    /// <summary>Alpha-weighted squared error of one encoded block: RGB error counts in proportion to visibility.</summary>
    private static double BlockError(byte[] enc16, ReadOnlySpan<uint> src)
    {
        var d = BlockDecoders.Value!.DecodeBlock(enc16.AsSpan(0, 16), CompressionFormat.Bc7).Span;
        double se = 0;
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                uint a = src[y * 4 + x];
                var c = d[y, x];
                double w = (a >> 24) / 255.0;
                int dr = (int)(a & 0xFF) - c.r, dg = (int)((a >> 8) & 0xFF) - c.g, db = (int)((a >> 16) & 0xFF) - c.b, da = (int)(a >> 24) - c.a;
                se += w * (dr * dr + dg * dg + db * db) + da * da;
            }
        return se;
    }
    private static byte[]? _transparentBlock;

    /// <summary>
    /// Encodes each mip of an RGBA8 chain to BC7 separately (our alpha-aware mips, not the encoder's) and concatenates.
    /// Block-level and parallel over block rows. Fully transparent blocks (most of an atlas page) reuse one pre-encoded
    /// block, and solid-colour blocks are cached, which is where nearly all the time would otherwise go.
    /// </summary>
    public static byte[] EncodeBc7(byte[] rgbaChain, int size, int mips, bool fresh = false)
    {
        if (fresh) { Encoders = new(NewEncoder); _transparentBlock = null; } // pick up a changed quality setting
        _transparentBlock ??= Encoders.Value!.EncodeBlock(new ColorRgba32[16].AsSpan());
        var transparent = _transparentBlock;
        // Refinement threshold as total block squared error equivalent to the requested PSNR over 16 RGBA texels.
        double refineSe = ConvertOptions.Bc7Quality == CompressionQuality.Fast && ConvertOptions.Bc7RefineDb > 0
            ? 64 * 255.0 * 255.0 / Math.Pow(10, ConvertOptions.Bc7RefineDb / 10.0) : 0;
        var outBuf = new byte[AtlasPage.ChainBytes(size, mips, PackPixelFormat.Bc7)];
        int src = 0, dst = 0;
        for (int m = 0; m < mips; m++)
        {
            int s = size >> m, bw = s / 4;
            int srcOff = src, dstOff = dst;
            var solid = new System.Collections.Concurrent.ConcurrentDictionary<uint, byte[]>();
            Parallel.For(0, bw, ConvertOptions.Par, by =>
            {
                var enc = Encoders.Value!;
                var px = MemoryMarshal.Cast<byte, uint>(rgbaChain.AsSpan(srcOff, s * s * 4));
                var blk = new ColorRgba32[16];
                var blkU = MemoryMarshal.Cast<ColorRgba32, uint>(blk.AsSpan());
                for (int bx = 0; bx < bw; bx++)
                {
                    uint or = 0, first = px[by * 4 * s + bx * 4];
                    bool uniform = true;
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            uint v = px[(by * 4 + y) * s + bx * 4 + x];
                            blkU[y * 4 + x] = v;
                            or |= v & 0xFF000000;
                            uniform &= v == first;
                        }
                    if (or != 0 && ConvertOptions.Bc7FillTransparent) FillTransparent(blkU);
                    byte[] enc16;
                    if (or == 0) enc16 = transparent;
                    else if (uniform) enc16 = solid.GetOrAdd(first, _ => enc.EncodeBlock(blk.AsSpan()));
                    else
                    {
                        enc16 = enc.EncodeBlock(blk.AsSpan());
                        Interlocked.Increment(ref _encoded);
                        // Error-driven refinement: blocks the fast mode handles badly (tiny high-contrast sprites, mixed
                        // alpha) are re-encoded at Balanced; the better of the two is kept.
                        if (refineSe > 0)
                        {
                            double e0 = BlockError(enc16, blkU);
                            if (e0 > refineSe)
                            {
                                var hq = Refiners.Value!.EncodeBlock(blk.AsSpan());
                                if (BlockError(hq, blkU) < e0) enc16 = hq;
                                Interlocked.Increment(ref _refined);
                            }
                        }
                    }
                    enc16.AsSpan(0, 16).CopyTo(outBuf.AsSpan(dstOff + (by * bw + bx) * 16, 16));
                }
            });
            src += AtlasPage.MipBytes(size, m);
            dst += AtlasPage.MipBytes(size, m, PackPixelFormat.Bc7);
        }
        return outBuf;
    }

    /// <summary>
    /// Gives fully transparent texels of a block the mean RGB of its visible texels (alpha stays 0). Otherwise the
    /// encoder spends endpoint precision on the black under transparency, and thin opaque features next to holes
    /// (handles, outlines, highlights) get crushed toward black. It also stops bilinear filtering pulling in dark fringes.
    /// </summary>
    private static void FillTransparent(Span<uint> b)
    {
        uint r = 0, g = 0, bl = 0, n = 0;
        bool any = false;
        foreach (var v in b)
        {
            if ((v >> 24) == 0) { any = true; continue; }
            r += v & 0xFF; g += (v >> 8) & 0xFF; bl += (v >> 16) & 0xFF; n++;
        }
        if (!any || n == 0) return;
        uint fill = (r / n) | ((g / n) << 8) | ((bl / n) << 16);
        for (int i = 0; i < b.Length; i++) if ((b[i] >> 24) == 0) b[i] = fill;
    }

    // Decode uses ClassicUO's own 5->8 bit table (HuesHelper.Color16To32), so decoded texels compare exactly with the
    // loader's RGBA output. That table is not exactly round(c*255/31) (e.g. 17 -> 0x8B), but (v*31+127)/255 and v>>3
    // both invert it exactly for all 32 codes.

    /// <summary>
    /// RGBA8 chain -> B5G5R5A1 chain. Colour channels are rounded to 5 bits ((v*31+127)/255, which maps UO's expanded
    /// values back to their original 5-bit codes exactly); alpha = a >= 128. Texels with alpha 0 are stored as 0.
    /// </summary>
    public static byte[] EncodeB5G5R5A1(ReadOnlySpan<byte> rgbaChain, int size, int mips)
    {
        var outBuf = new byte[AtlasPage.ChainBytes(size, mips, PackPixelFormat.B5G5R5A1)];
        var src = MemoryMarshal.Cast<byte, uint>(rgbaChain);
        var dst = MemoryMarshal.Cast<byte, ushort>(outBuf.AsSpan());
        int n = 0;
        for (int m = 0; m < mips; m++) n += (size >> m) * (size >> m);
        for (int i = 0; i < n; i++)
        {
            uint v = src[i];
            if ((v >> 24) < 128) { dst[i] = 0; continue; }
            uint r = ((v & 0xFF) * 31 + 127) / 255, g = (((v >> 8) & 0xFF) * 31 + 127) / 255, b = (((v >> 16) & 0xFF) * 31 + 127) / 255;
            dst[i] = (ushort)(b | (g << 5) | (r << 10) | 0x8000);
        }
        return outBuf;
    }

    public static uint[] DecodeB5G5R5A1(ReadOnlySpan<byte> data)
    {
        var src = MemoryMarshal.Cast<byte, ushort>(data);
        var o = new uint[src.Length];
        for (int i = 0; i < o.Length; i++)
        {
            ushort c = src[i];
            if ((c & 0x8000) == 0) continue;
            o[i] = ClassicUO.Utility.HuesHelper.Color16To32((ushort)(c & 0x7FFF)) | 0xFF000000;
        }
        return o;
    }

    public static uint[] DecodeBc7(ReadOnlySpan<byte> blocks, int s)
    {
        var dec = new BcDecoder();
        dec.Options.IsParallel = false;
        var px = dec.DecodeRaw(blocks.ToArray(), s, s, CompressionFormat.Bc7);
        return MemoryMarshal.Cast<ColorRgba32, uint>(px.AsSpan()).ToArray();
    }

    /// <summary>Reads one mip of a page file as RGBA8 regardless of its stored format.</summary>
    public static uint[] ReadMip(string path, int mip, out PageHeader header)
    {
        var fs = AtlasPage.OpenChain(path, out header);
        if (header.Format == PackPixelFormat.Indexed8 && mip > 0)
        {
            fs.Dispose();
            return ReadMip(AnimConst.MipsPath(Path.GetDirectoryName(path)!, IndexedPages.PageOf(path)), mip - 1, out _);
        }
        using var _fs = fs;
        int size = header.Width;
        int skip = AtlasPage.MipOffset(size, mip, header.Format);
        if (fs.CanSeek) fs.Position += skip;
        else
        {
            var junk = new byte[1 << 16];
            while (skip > 0) { int n = fs.Read(junk, 0, Math.Min(skip, junk.Length)); if (n <= 0) throw new EndOfStreamException(); skip -= n; }
        }
        var data = new byte[AtlasPage.MipBytes(size, mip, header.Format)];
        fs.ReadExactly(data);
        return header.Format switch
        {
            PackPixelFormat.Rgba8 => MemoryMarshal.Cast<byte, uint>(data).ToArray(),
            PackPixelFormat.Bc7 => DecodeBc7(data, size >> mip),
            PackPixelFormat.B5G5R5A1 => DecodeB5G5R5A1(data),
            PackPixelFormat.Indexed8 => IndexedPages.Expand(path, data, size),
            _ => throw new NotSupportedException(header.Format.ToString()),
        };
    }
}
