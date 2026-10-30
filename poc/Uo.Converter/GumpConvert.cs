using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.IO;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// gumps/gumps.bin (docs/modern-client/pack-format.md, "Gumps"): every gump id the client's GumpsLoader resolves,
/// with gump.def aliases and loose Gumps/*.gump files already applied. One record per distinct image (canonical RLE,
/// <see cref="GumpCodec"/>), compressed on its own; ids map to records, gump.def hues ride on the slot.
/// </summary>
public static class GumpConvert
{
    /// <summary>--raw-gumps: store records uncompressed (codec 0).</summary>
    public static bool Raw;
    public const int Quality = 9;
    /// <summary>Ship: raw texels with Brotli (smallest, fastest Brotli decode). --raw-gumps: the RLE rows uncompressed (fastest decode).</summary>
    public static ushort Layout => Raw ? GumpCodec.LayoutRle : GumpCodec.LayoutTexels;
    /// <summary>
    /// --gump-small n: an image whose run-length payload is at most n bytes is stored that way, uncompressed (a Brotli
    /// decode has a fixed cost of tens of µs that dominates small images; they add little to the file). 0 = off.
    /// </summary>
    public static int SmallRle = 65536;

    public sealed record Stats(int Slots, int Present, int Records, int Aliases, int ContentShared, int Hued, int SmallRaw, long RawBytes, long StoredBytes, long FileBytes)
    {
        public override string ToString() =>
            $"{Slots} slots, {Present} gumps, {Records} records ({Aliases} gump.def/index aliases, {ContentShared} identical images shared, {Hued} hued; {SmallRaw} small images stored run-length uncompressed), " +
            $"payload {RawBytes / 1048576.0:F1} MiB -> {StoredBytes / 1048576.0:F1} MiB, file {FileBytes / 1048576.0:F1} MiB";
    }

    /// <summary>Per-thread GumpsLoaders (the UOFile keeps a seek position), with the main loader's entries (gump.def applied).</summary>
    private sealed class Worker : IDisposable
    {
        public readonly UOFileManager Fm;

        public Worker(UOFileManager main)
        {
            Fm = new UOFileManager(main.Version, main.BasePath);
            Fm.Gumps.DefPath = main.Gumps.DefPath;
            Fm.Gumps.Load();
            var src = main.Gumps.File.Entries;
            var dst = Fm.Gumps.File.Entries;
            if (src.Length != dst.Length) throw new InvalidOperationException("gump entry count mismatch");
            for (int i = 0; i < src.Length; i++)
            {
                var e = src[i];
                if (e.File == main.Gumps.File) e.File = Fm.Gumps.File; // .mul entries point at their own file
                else if (e.File != null) throw new InvalidOperationException("gump entry backed by a foreign file");
                dst[i] = e;
            }
        }

        public void Dispose() => Fm.Dispose();
    }

    public static Stats Run(UOFileManager fm, string outDir)
    {
        var loader = fm.Gumps;
        if (loader.File == null) loader.Load();
        var entries = loader.File!.Entries;
        var ours = new HashSet<int>(loader.OursIds);

        // Which ids the client resolves, and which share an index entry (gump.def copies the entry; UOP/MUL may too).
        // An id with a loose file of ours is decoded on its own (its file replaces the entry).
        var canonical = new int[entries.Length];
        var byEntry = new Dictionary<(long, int, int), int>();
        for (int id = 0; id < entries.Length; id++)
        {
            canonical[id] = -1;
            ref var e = ref entries[id];
            bool archive = e.CompressionFlag == CompressionType.ZlibBwt || e.Width > 0 || e.Height > 0;
            if (ours.Contains(id)) { canonical[id] = id; continue; }
            if (!archive) continue;
            var key = (e.Offset, e.Length, e.DecompressedLength);
            canonical[id] = byEntry.TryGetValue(key, out int first) ? first : (byEntry[key] = id);
        }

        var images = new (ushort[]? Texels, int W, int H, bool Ours)[entries.Length];
        using (var workers = new ThreadLocal<Worker>(() => new Worker(fm), trackAllValues: true))
        {
            Parallel.For(0, entries.Length, ConvertOptions.Par, id =>
            {
                if (canonical[id] != id) return;
                var t = workers.Value!.Fm.Gumps.GetGump16((uint)id, out int w, out int h, out bool fromOurs);
                if (t != null && w > 0 && h > 0) images[id] = (t, w, h, fromOurs);
            });
            foreach (var w in workers.Values) w.Dispose();
        }

        // An id of ours whose file was unusable fell back to the archive: it is then an ordinary entry user.
        int highest = -1;
        for (int id = 0; id < entries.Length; id++)
        {
            int c = canonical[id];
            if (c >= 0 && images[c].Texels != null) highest = id;
        }

        // One record per distinct (size, texels).
        var writer = new RecordStoreWriter(highest + 1);
        var recordOf = new Dictionary<int, int>();          // canonical id -> record
        var byContent = new Dictionary<(int, int, int), List<(int Id, int Record)>>();
        var encoded = new byte[entries.Length][];
        var layouts = new ushort[entries.Length];
        Parallel.For(0, entries.Length, ConvertOptions.Par, id =>
        {
            var img = images[id];
            if (img.Texels == null) return;
            var rle = GumpCodec.EncodeRle(img.Texels, img.W, img.H);
            bool useRle = Raw || rle.Length <= SmallRle;
            layouts[id] = useRle ? GumpCodec.LayoutRle : GumpCodec.LayoutTexels;
            encoded[id] = useRle ? rle : GumpCodec.Encode(GumpCodec.LayoutTexels, img.Texels, img.W, img.H);
        });
        int present = 0, aliases = 0, shared = 0, hued = 0;
        long rawBytes = 0;
        var toCompress = new List<(int Record, byte[] Raw, ushort Codec)>();
        int small = 0;
        for (int id = 0; id <= highest; id++)
        {
            int c = canonical[id];
            if (c < 0 || images[c].Texels == null) continue;
            if (!recordOf.TryGetValue(c, out int rec))
            {
                var img = images[c];
                var hc = new HashCode();
                hc.AddBytes(MemoryMarshal.AsBytes(img.Texels.AsSpan()));
                var ck = (hc.ToHashCode(), img.W, img.H);
                rec = -1;
                if (byContent.TryGetValue(ck, out var list))
                    foreach (var (other, r) in list)
                        if (images[other].Texels.AsSpan().SequenceEqual(img.Texels)) { rec = r; shared++; break; }
                if (rec < 0)
                {
                    ushort layout = layouts[c];
                    ushort codec = layout == GumpCodec.LayoutRle ? StoreConst.CodecRaw : StoreConst.CodecBrotli;
                    if (!Raw && layout == GumpCodec.LayoutRle) small++;
                    rec = writer.Add([], (uint)encoded[c].Length, codec, (ushort)img.W, (ushort)img.H, layout);
                    toCompress.Add((rec, encoded[c], codec));
                    rawBytes += encoded[c].Length;
                    (byContent.TryGetValue(ck, out list) ? list : byContent[ck] = new()).Add((c, rec));
                }
                recordOf[c] = rec;
            }
            else aliases++;
            // ReadOurs decodes without a hue; the archive path uses the entry's (gump.def) hue
            ushort hue = images[c].Ours && c == id ? (ushort)0 : entries[id].Hue;
            if (hue != 0) hued++;
            writer.Slots[id] = new StoreSlot { Record = (uint)rec + 1, Arg = hue };
            present++;
        }

        var stored = new byte[toCompress.Count][];
        Parallel.For(0, toCompress.Count, ConvertOptions.Par, i =>
            stored[i] = RecordStoreWriter.Encode(toCompress[i].Raw, toCompress[i].Codec, Quality));
        long storedBytes = 0;
        for (int i = 0; i < toCompress.Count; i++)
        {
            writer.SetPayload(toCompress[i].Record, stored[i]);
            storedBytes += stored[i].Length;
        }

        var path = Path.Combine(outDir, StoreConst.GumpsFile);
        writer.Write(path, StoreConst.MagicGumps, loader.UseUOPGumps ? StoreConst.GumpFlagUop : 0);
        return new Stats(highest + 1, present, writer.RecordCount, aliases, shared, hued, small, rawBytes, storedBytes, new FileInfo(path).Length);
    }
}
