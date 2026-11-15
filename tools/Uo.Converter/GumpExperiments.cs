using System.Diagnostics;
using System.IO.Compression;
using ClassicUO.Assets;
using ClassicUO.IO;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// Gump storage investigation (--gump-report; docs/modern-client/pack-format.md, "Gumps"): sizes of the candidate
/// encodings over every gump, and per-gump decode latency of each against the client's own MUL/UOP decode.
/// </summary>
public static class GumpExperiments
{
    public static int Report(UOFileManager fm)
    {
        var loader = fm.Gumps;
        loader.Load();
        var entries = loader.File.Entries;
        var seen = new Dictionary<(UOFile?, long, int), int>();
        var ids = new List<int>();
        int aliases = 0, present = 0;
        for (int id = 0; id < entries.Length; id++)
        {
            ref var e = ref entries[id];
            if (e.Length <= 0 && e.Width <= 0) continue;
            present++;
            if (seen.ContainsKey((e.File, e.Offset, e.Length))) { aliases++; continue; }
            seen[(e.File, e.Offset, e.Length)] = id;
            ids.Add(id);
        }
        Console.WriteLine($"gumps: {entries.Length} slots, {present} present, {ids.Count} unique, {aliases} aliases (gump.def), uop={loader.UseUOPGumps}, highest {ids.Max()}");

        long rawZ = 0, rleZ = 0, srcBytes = 0, pixels = 0, raw16 = 0, rle = 0, rawQ5 = 0, rleQ5 = 0, rleQ9 = 0, rawQ9 = 0, cells = 0, transparent = 0;
        int maxW = 0, maxH = 0;
        var sizes = new List<(int Id, int W, int H)>();
        const int Batch = 512;
        for (int b = 0; b < ids.Count; b += Batch)
        {
            var batch = ids.Skip(b).Take(Batch).Select(id =>
            {
                var t = loader.GetGump16((uint)id, out int w, out int h, out _);
                return (id, t, w, h, entries[id].Length);
            }).Where(x => x.t != null).ToArray();
            foreach (var x in batch)
            {
                srcBytes += x.Length;
                pixels += (long)x.w * x.h;
                cells += (long)(x.w + 4) * (x.h + 4);
                maxW = Math.Max(maxW, x.w); maxH = Math.Max(maxH, x.h);
                sizes.Add((x.id, x.w, x.h));
                foreach (var v in x.t!) if (v == 0) transparent++;
            }
            var res = new long[batch.Length, 8];
            Parallel.For(0, batch.Length, ConvertOptions.Par, i =>
            {
                var t = batch[i].t!;
                var rawBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(t.AsSpan()).ToArray();
                var rleBytes = GumpCodec.EncodeRle(t, batch[i].w, batch[i].h);
                res[i, 0] = rawBytes.Length;
                res[i, 1] = rleBytes.Length;
                res[i, 2] = Brotli(rawBytes, 5);
                res[i, 3] = Brotli(rleBytes, 5);
                res[i, 4] = Brotli(rleBytes, 9);
                res[i, 5] = Brotli(rawBytes, 9);
                res[i, 6] = DeflateBytes(rawBytes).Length;
                res[i, 7] = DeflateBytes(rleBytes).Length;
            });
            for (int i = 0; i < batch.Length; i++)
            {
                raw16 += res[i, 0]; rle += res[i, 1]; rawQ5 += res[i, 2]; rleQ5 += res[i, 3]; rleQ9 += res[i, 4]; rawQ9 += res[i, 5]; rawZ += res[i, 6]; rleZ += res[i, 7];
            }
        }
        double M(long v) => v / 1048576.0;
        Console.WriteLine($"  decoded: {pixels / 1e6:F1} M texels ({100.0 * transparent / pixels:F1}% transparent), largest {maxW}x{maxH}");
        Console.WriteLine($"  source entries (MUL/UOP RLE as stored): {M(srcBytes):F1} MiB");
        Console.WriteLine($"  raw B5G5R5A1:   {M(raw16):F1} MiB   Brotli q5 per gump {M(rawQ5):F1} MiB   q9 {M(rawQ9):F1} MiB   zlib (smallest) {M(rawZ):F1} MiB");
        Console.WriteLine($"  canonical RLE:  {M(rle):F1} MiB   Brotli q5 per gump {M(rleQ5):F1} MiB   q9 {M(rleQ9):F1} MiB   zlib (smallest) {M(rleZ):F1} MiB");
        Console.WriteLine($"  atlas estimate (2 px gutters, 100% occupancy, no mips): {M(cells * 2):F1} MiB raw = {cells * 2.0 / (2048.0 * 2048 * 2):F1} pages of 2048^2");

        // Small images: per-gump decode cost by run-length payload size (every gump, each timed as the best of 3).
        {
            long[] edges = [1024, 4096, 16384, 65536, long.MaxValue];
            var n = new int[edges.Length];
            var rleSum = new long[edges.Length];
            var brSum = new long[edges.Length];
            var tBr = new double[edges.Length];
            var tRle = new double[edges.Length];
            var dst = new uint[800 * 800];
            var tmp = new byte[800 * 800 * 2];
            foreach (var (id, _, _) in sizes)
            {
                var t = loader.GetGump16((uint)id, out int w, out int h, out _)!;
                var rawBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(t.AsSpan()).ToArray();
                var rleBytes = GumpCodec.EncodeRle(t, w, h);
                var br = BrotliBytes(rawBytes, 9);
                int bkt = Array.FindIndex(edges, e => rleBytes.Length <= e);
                double best1 = double.MaxValue, best2 = double.MaxValue;
                for (int r = 0; r < 3; r++)
                {
                    long s0 = Stopwatch.GetTimestamp();
                    BrotliDecoder.TryDecompress(br, tmp, out int nb);
                    GumpCodec.ExpandTexels(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(tmp.AsSpan(0, nb)), dst, Lut, default);
                    long s1 = Stopwatch.GetTimestamp();
                    GumpCodec.DecodeRle(rleBytes, w, h, dst, Expand, default);
                    long s2 = Stopwatch.GetTimestamp();
                    best1 = Math.Min(best1, (s1 - s0) * 1e6 / Stopwatch.Frequency);
                    best2 = Math.Min(best2, (s2 - s1) * 1e6 / Stopwatch.Frequency);
                }
                n[bkt]++; rleSum[bkt] += rleBytes.Length; brSum[bkt] += br.Length; tBr[bkt] += best1; tRle[bkt] += best2;
            }
            Console.WriteLine("  per-gump decode by run-length payload size: count, RLE MiB, texels+Brotli MiB, mean us Brotli texels+expand, mean us RLE fill");
            for (int b = 0; b < edges.Length; b++)
                Console.WriteLine($"    <= {(edges[b] == long.MaxValue ? "max" : (edges[b] / 1024) + " KiB"),7}: {n[b],5}  {M(rleSum[b]),7:F1}  {M(brSum[b]),7:F1}  {tBr[b] / Math.Max(1, n[b]),8:F1}  {tRle[b] / Math.Max(1, n[b]),8:F1}");
        }

        // Latency: the largest gumps plus the ones the main windows open.
        var named = new (int Id, string Name)[] { (0x07D0, "paperdoll"), (0x08AC, "spellbook"), (0x2A6C, "status (new)"), (0x0802, "status (old)"), (0x003C, "backpack"), (0x0A3C, "resizepic tl") };
        var probe = named.Where(n => sizes.Any(s => s.Id == n.Id)).Select(n => (n.Id, n.Name))
            .Concat(sizes.OrderByDescending(s => (long)s.W * s.H).Take(6).Select(s => (s.Id, "large"))).Distinct().ToArray();
        Console.WriteLine("  latency per gump, median of 31 (us): client MUL/UOP decode | Brotli raw16 + expand | Brotli RLE + fill | RLE fill (no Brotli) | zlib raw16 + expand | zlib RLE + fill");
        foreach (var (id, name) in probe)
        {
            var t = loader.GetGump16((uint)id, out int w, out int h, out _)!;
            var rawBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(t.AsSpan()).ToArray();
            var rleBytes = GumpCodec.EncodeRle(t, w, h);
            var rawC = BrotliBytes(rawBytes, 5);
            var rleC = BrotliBytes(rleBytes, 5);
            var dst = new uint[w * h];
            var tmp = new byte[Math.Max(rawBytes.Length, rleBytes.Length)];
            double mul = Median(() => { var g = loader.GetGump((uint)id); if (g.Width != w) throw new InvalidOperationException(); });
            double braw = Median(() =>
            {
                BrotliDecoder.TryDecompress(rawC, tmp, out int n);
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(tmp.AsSpan(0, n));
                GumpCodec.ExpandTexels(src, dst, Lut, default);
            });
            double brle = Median(() =>
            {
                BrotliDecoder.TryDecompress(rleC, tmp, out int n);
                GumpCodec.DecodeRle(tmp.AsSpan(0, n), w, h, dst, Expand, default);
            });
            double frle = Median(() => GumpCodec.DecodeRle(rleBytes, w, h, dst, Expand, default));
            var rawZc = DeflateBytes(rawBytes);
            var rleZc = DeflateBytes(rleBytes);
            double zraw = Median(() =>
            {
                int n = Inflate(rawZc, tmp);
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(tmp.AsSpan(0, n));
                GumpCodec.ExpandTexels(src, dst, Lut, default);
            });
            double bonly = Median(() => BrotliDecoder.TryDecompress(rawC, tmp, out _));
            double zonly = Median(() => Inflate(rawZc, tmp));
            double lut = Median(() => GumpCodec.ExpandTexels(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(rawBytes), dst, Lut, default));
            double zrle = Median(() =>
            {
                int n = Inflate(rleZc, tmp);
                GumpCodec.DecodeRle(tmp.AsSpan(0, n), w, h, dst, Expand, default);
            });
            Console.WriteLine($"    0x{id:X4} {name,-13} {w,4}x{h,-4} raw {rawBytes.Length / 1024,5} KiB rle {rleBytes.Length / 1024,4} KiB (q5 {rawC.Length / 1024,4} / {rleC.Length / 1024,4} KiB): {mul,8:F0} | {braw,8:F0} | {brle,8:F0} | {frle,8:F0} | {zraw,8:F0} | {zrle,8:F0}  (zlib {rawZc.Length / 1024} / {rleZc.Length / 1024} KiB); raw16 alone: brotli {bonly:F0} zlib {zonly:F0} LUT expand {lut:F0}");
        }
        return 0;
    }

    /// <summary>ClassicUO's 5->8 bit table (HuesHelper.Color16To32, red channel), as the client's decode uses it.</summary>
    public static readonly uint[] Expand = Enumerable.Range(0, 32).Select(c => ClassicUO.Utility.HuesHelper.Color16To32((ushort)(c << 10)) & 0xFF).ToArray();

    public static readonly uint[] Lut = GumpCodec.BuildLut(Expand);

    private static long Brotli(byte[] data, int q) => BrotliBytes(data, q).Length;

    private static byte[] DeflateBytes(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static int Inflate(byte[] data, byte[] dst)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        int n = 0, r;
        while ((r = z.Read(dst, n, dst.Length - n)) > 0) n += r;
        return n;
    }

    private static byte[] BrotliBytes(byte[] data, int q)
    {
        var buf = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, buf, out int n, q, 22)) throw new InvalidOperationException();
        return buf.AsSpan(0, n).ToArray();
    }

    private static double Median(Action a)
    {
        var t = new double[31];
        a();
        for (int i = 0; i < t.Length; i++)
        {
            long s = Stopwatch.GetTimestamp();
            a();
            t[i] = (Stopwatch.GetTimestamp() - s) * 1e6 / Stopwatch.Frequency;
        }
        Array.Sort(t);
        return t[t.Length / 2];
    }
}
