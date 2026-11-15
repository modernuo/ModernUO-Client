using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>
/// Animation storage investigation (docs/modern-client/pack-format-v0.md, "Animation storage investigation"):
/// chunked Brotli sizes and decode latency, per-creature page footprints, Indexed8 losslessness, and a CPU reference
/// of the manual bilinear filter an Indexed8 shader would use.
/// </summary>
public static class AnimExperiments
{
    private static readonly int[] ChunkSizes = [256 << 10, 1 << 20, 4 << 20];

    /// <summary>Logical payload of a page file (PageHeader + chain), whatever its kind.</summary>
    private static byte[] Logical(string path)
    {
        using var s = AtlasPage.OpenChain(path, out var h);
        int hs = StructIo.SizeOf<PageHeader>();
        var b = new byte[hs + h.DataBytes];
        MemoryMarshal.Write(b, in h);
        s.ReadExactly(b.AsSpan(hs));
        return b;
    }

    /// <summary>--bench-chunked &lt;anim dir&gt; [out dir]: Kind 1 vs Kind 2 sizes over every page; decode latency on a sample.</summary>
    public static int BenchChunked(string dir, string? outDir)
    {
        var files = Directory.GetFiles(dir, "page_*.bin").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        outDir ??= Path.Combine(Path.GetTempPath(), "uo-chunked-bench");
        PublishedPacks.EnsureOutputDir(outDir); // the process-wide roots, --packs-root included (ConfigureFromArgs)
        Directory.CreateDirectory(outDir);
        foreach (var f in new[] { AnimTableFile.FileName, AnimDataFile.FileName, AnimPaletteFile.FileName })
            if (File.Exists(Path.Combine(dir, f))) ReplacingFile.Copy(Path.Combine(dir, f), Path.Combine(outDir, f));
        long raw = 0, onDisk = 0, k1 = 0;
        var k2 = new long[ChunkSizes.Length];
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(files, ConvertOptions.Par, f =>
        {
            var logical = Logical(f);
            var buf = new byte[BrotliEncoder.GetMaxCompressedLength(logical.Length)];
            if (!BrotliEncoder.TryCompress(logical, buf, out int n1, ChunkedBrotli.Quality, ChunkedBrotli.Window)) throw new InvalidOperationException();
            Interlocked.Add(ref raw, logical.Length + 16);
            Interlocked.Add(ref onDisk, new FileInfo(f).Length);
            Interlocked.Add(ref k1, n1 + 16);
            for (int c = 0; c < ChunkSizes.Length; c++)
            {
                using var ms = new MemoryStream();
                StructIo.WriteHeader(ms, PackConst.MagicPage, FileKind.ChunkedBrotli);
                ChunkedBrotli.Write(ms, logical, ChunkSizes[c]);
                Interlocked.Add(ref k2[c], ms.Length);
                if (ChunkSizes[c] == ChunkedBrotli.DefaultChunkBytes)
                    ReplacingFile.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(f)), ms.ToArray());
            }
        });
        Console.WriteLine($"{files.Length} pages from {dir} ({sw.Elapsed.TotalSeconds:F0}s): raw {raw / 1048576.0:F1} MiB, on disk {onDisk / 1048576.0:F1} MiB");
        Console.WriteLine($"  Kind 1 (one stream, q5 lgwin22): {k1 / 1048576.0:F1} MiB ({100.0 * k1 / raw:F2}% of raw)");
        for (int c = 0; c < ChunkSizes.Length; c++)
            Console.WriteLine($"  Kind 2 chunks {ChunkSizes[c] >> 10,5} KiB: {k2[c] / 1048576.0:F1} MiB ({100.0 * k2[c] / raw:F2}% of raw, {100.0 * (k2[c] - k1) / k1:+0.00;-0.00}% vs Kind 1)");

        // Decode latency: 32 pages spread over the set, warm file cache, each timed 5x (median).
        var sample = Enumerable.Range(0, Math.Min(32, files.Length)).Select(i => files[(int)((long)i * files.Length / Math.Min(32, files.Length))]).ToArray();
        var dst = new byte[sample.Max(f => Logical(f).Length)];
        var kind1Ms = new List<double>();
        foreach (var f in sample)
        {
            var bytes = File.ReadAllBytes(f);
            var times = new double[5];
            for (int r = 0; r < times.Length; r++)
            {
                var t = Stopwatch.StartNew();
                if (!BrotliDecoder.TryDecompress(Brotli(bytes), dst, out _)) throw new InvalidDataException(f);
                times[r] = t.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            kind1Ms.Add(times[2]);
        }
        Console.WriteLine($"  decode Kind 1, 1 thread (in-memory one-shot): median {Median(kind1Ms):F1} ms, mean {kind1Ms.Average():F1} ms per page");
        foreach (int workers in new[] { 1, 2, 4, 8 })
        {
            using var dec = new ChunkedBrotliDecoder(workers);
            var ms = new List<double>();
            long alloc = 0;
            foreach (var f in sample)
            {
                var k2Path = Path.Combine(outDir, Path.GetFileName(f));
                AtlasPage.Read(k2Path, dst, dec); // warm-up
                var times = new double[5];
                long a0 = GC.GetTotalAllocatedBytes(true);
                for (int r = 0; r < times.Length; r++)
                {
                    var t = Stopwatch.StartNew();
                    AtlasPage.Read(k2Path, dst, dec);
                    times[r] = t.Elapsed.TotalMilliseconds;
                }
                alloc += GC.GetTotalAllocatedBytes(true) - a0;
                Array.Sort(times);
                ms.Add(times[2]);
            }
            Console.WriteLine($"  decode Kind 2 (1 MiB chunks, file read + decode), {workers} worker(s): median {Median(ms):F1} ms, mean {ms.Average():F1} ms per page; " +
                              $"managed alloc {alloc / (sample.Length * 5.0):F0} B per page read");
        }
        // Correctness: Kind 2 decode == logical payload.
        using (var dec = new ChunkedBrotliDecoder(8))
            foreach (var f in sample.Take(4))
            {
                var logical = Logical(f);
                var h = AtlasPage.Read(Path.Combine(outDir, Path.GetFileName(f)), dst, dec);
                if (!dst.AsSpan(0, (int)h.DataBytes).SequenceEqual(logical.AsSpan(16)) || h.DataBytes != logical.Length - 16) throw new InvalidDataException($"kind 2 mismatch {f}");
                if (!Logical(Path.Combine(outDir, Path.GetFileName(f))).AsSpan().SequenceEqual(logical)) throw new InvalidDataException($"kind 2 stream mismatch {f}");
            }
        Console.WriteLine("  Kind 2 round trip (parallel and stream readers) bit-exact on 4 sample pages");
        return 0;
    }

    private static ReadOnlySpan<byte> Brotli(byte[] file)
    {
        var fh = MemoryMarshal.Read<FileHeader>(file);
        if (fh.Kind != FileKind.Brotli) throw new InvalidDataException("expected Kind 1 pages for the Kind 1 timing");
        return file.AsSpan(16);
    }

    private static double Median(List<double> l) { var c = l.ToArray(); Array.Sort(c); return c[c.Length / 2]; }

    // Human: walk/run armed and unarmed + stand (PeopleAnimationGroup 0..4). Monster: walk + stand (High 0, 1).
    // Animal: walk, run, stand (Low 0..2). "All" = every action of the body.
    private static int[] MoveIdle(AnimType t) => t switch
    {
        AnimType.Human or AnimType.Equipment => [0, 1, 2, 3, 4],
        AnimType.Animal => [0, 1, 2],
        _ => [0, 1],
    };

    private static readonly (int Body, string Name)[] Creatures =
    [
        (400, "human male"), (401, "human female"), (1, "ogre"), (3, "zombie"), (9, "daemon"), (17, "orc"), (18, "ettin"),
        (24, "lich"), (50, "skeleton"), (53, "troll"), (59, "dragon"), (200, "horse"), (225, "timber wolf"), (51, "slime"),
    ];

    /// <summary>--anim-report &lt;pack[,pack...]&gt;: pages, sizes, packing efficiency, and pages/bytes to show each creature.</summary>
    public static int Report(string packs)
    {
        foreach (var pack in packs.Split(','))
        {
            var dir = Path.Combine(pack, "anim");
            var (bodies, dirs, frames) = AnimTableFile.Read(Path.Combine(dir, AnimTableFile.FileName));
            var files = Directory.GetFiles(dir, "page_*.bin");
            var pages = new (long Raw, long File, long MipRaw, long MipFile, long Vram)[files.Length];
            int S = 0; PixelFormat fmt = default;
            for (int p = 0; p < files.Length; p++)
            {
                using (var s = AtlasPage.OpenChain(PackConst.PagePath(dir, p), out var h)) { S = h.Width; fmt = h.Format; pages[p].Raw = h.DataBytes + 32; }
                pages[p].File = new FileInfo(PackConst.PagePath(dir, p)).Length;
                var mp = AnimConst.MipsPath(dir, p);
                if (File.Exists(mp))
                {
                    using (var s = AtlasPage.OpenChain(mp, out var mh)) pages[p].MipRaw = mh.DataBytes + 32;
                    pages[p].MipFile = new FileInfo(mp).Length;
                }
                pages[p].Vram = pages[p].Raw - 32 + Math.Max(0, pages[p].MipRaw - 32);
            }
            var seen = new HashSet<(int, int, int)>();
            long spriteTexels = 0, cellTexels = 0;
            foreach (var f in frames)
            {
                var e = f.Sprite;
                if (!e.Present || !seen.Add((e.Page, e.X, e.Y))) continue;
                spriteTexels += (long)e.W * e.H;
                cellTexels += (long)(e.W + 4) * (e.H + 4);
            }
            long pageTexels = (long)files.Length * S * S;
            long palBytes = File.Exists(Path.Combine(dir, AnimPaletteFile.FileName)) ? new FileInfo(Path.Combine(dir, AnimPaletteFile.FileName)).Length : 0;
            long palBrotli = 0;
            if (palBytes > 0)
            {
                var pb = File.ReadAllBytes(Path.Combine(dir, AnimPaletteFile.FileName));
                var cb = new byte[BrotliEncoder.GetMaxCompressedLength(pb.Length)];
                BrotliEncoder.TryCompress(pb, cb, out int n, ChunkedBrotli.Quality, ChunkedBrotli.Window);
                palBrotli = n;
            }
            Console.WriteLine($"{pack}: {files.Length} anim pages of {S}^2 ({fmt}), raw {pages.Sum(x => x.Raw) / 1048576.0:F1} MiB + mips sidecar {pages.Sum(x => x.MipRaw) / 1048576.0:F1} MiB, " +
                              $"on disk {pages.Sum(x => x.File) / 1048576.0:F1} + {pages.Sum(x => x.MipFile) / 1048576.0:F1} MiB, palettes.bin {palBytes / 1024.0:F0} KiB (Brotli {palBrotli / 1024.0:F0} KiB); " +
                              $"sprite texels/page texels {100.0 * spriteTexels / pageTexels:F1}%, cells incl. gutter {100.0 * cellTexels / pageTexels:F1}%");

            HashSet<int> Touched(int body, int[]? actions)
            {
                var set = new HashSet<int>();
                if (body >= bodies.Length) return set;
                if (bodies[body].AliasBody != AnimConst.NoBody && !bodies[body].HasFrames) body = bodies[body].AliasBody;
                var be = bodies[body];
                foreach (int a in actions ?? Enumerable.Range(0, be.ActionCount).ToArray())
                    for (int d = 0; d < AnimConst.Directions; d++)
                        foreach (var f in AnimTableFile.Frames(bodies, dirs, frames, body, a, d, out _))
                            if (f.Sprite.Present) set.Add(f.Sprite.Page);
                return set;
            }
            string Fmt(HashSet<int> set) =>
                $"{set.Count,3} pages, raw {set.Sum(p => pages[p].Raw + pages[p].MipRaw) / 1048576.0,6:F1} MiB, file {set.Sum(p => pages[p].File + pages[p].MipFile) / 1048576.0,6:F1} MiB (mip0 only {set.Sum(p => pages[p].File) / 1048576.0:F1}), VRAM {set.Sum(p => pages[p].Vram) / 1048576.0,6:F1} MiB";
            var unionMove = new HashSet<int>(); var unionAll = new HashSet<int>();
            foreach (var (b, name) in Creatures)
            {
                if (b >= bodies.Length) continue;
                var mv = Touched(b, MoveIdle(bodies[b].Type));
                var all = Touched(b, null);
                if (b >= 400) { } else { unionMove.UnionWith(mv); unionAll.UnionWith(all); }
                Console.WriteLine($"  {b,4} {name,-13} move+idle: {Fmt(mv)} | all actions: {Fmt(all)}");
            }
            Console.WriteLine($"  monster set (12 bodies) move+idle: {Fmt(unionMove)} | all: {Fmt(unionAll)}");
        }
        return 0;
    }

    /// <summary>--compare-indexed &lt;indexed pack&gt; &lt;b16 pack&gt;: palette[index] vs the B5G5R5A1 pack's mip0 (every texel), and the mips sidecar vs its mips 1+.</summary>
    /// <summary>
    /// --compare-bodies: which body ids draw differently in two packs' anim.bin (entry fields, and each direction's frame
    /// count, centers and sprite geometry; page placement is ignored). Bodies whose Body.def / Corpse.def alias points at
    /// a changed body are listed separately: their own entry is the same, but they draw the changed art.
    /// </summary>
    public static int CompareBodies(string packA, string packB)
    {
        var (ba, da, fa) = AnimTableFile.Read(Path.Combine(packA, "anim", AnimTableFile.FileName));
        var (bb, db, fb) = AnimTableFile.Read(Path.Combine(packB, "anim", AnimTableFile.FileName));
        static string Sig(AnimBodyEntry[] bodies, AnimDirEntry[] dirs, AnimFrameEntry[] frames, int b)
        {
            if (b >= bodies.Length) return "absent";
            var e = bodies[b];
            var sb = new System.Text.StringBuilder($"{e.Type} {e.Flags:X} {e.Source} {e.SourceBody} {e.MountHeight} {e.MountShift} {e.AliasBody}/{e.AliasHue} {e.CorpseBody}/{e.CorpseHue} {e.ActionCount};");
            for (int i = 0; i < e.ActionCount * AnimConst.Directions; i++)
            {
                var d = dirs[e.FirstDir + i];
                sb.Append($"|{d.FrameCount},{d.StoredDir},{d.Flags}");
                for (int k = 0; k < d.FrameCount; k++)
                {
                    var f = frames[d.FirstFrame + k];
                    var sp = f.Sprite;
                    sb.Append($":{f.CenterX},{f.CenterY},{sp.Present},{sp.OrigW},{sp.OrigH},{sp.W},{sp.H},{sp.TrimX},{sp.TrimY}");
                }
            }
            return sb.ToString();
        }
        int n = Math.Max(ba.Length, bb.Length);
        var changed = new List<int>();
        for (int b = 0; b < n; b++)
            if (Sig(ba, da, fa, b) != Sig(bb, db, fb, b)) changed.Add(b);
        var set = changed.ToHashSet();
        var viaAlias = new List<string>();
        for (int b = 0; b < Math.Min(ba.Length, bb.Length); b++)
        {
            if (set.Contains(b)) continue;
            var e = bb[b];
            if (e.AliasBody != AnimConst.NoBody && set.Contains(e.AliasBody)) viaAlias.Add($"{b}->{e.AliasBody} (Body.def)");
            if (e.CorpseBody != AnimConst.NoBody && set.Contains(e.CorpseBody)) viaAlias.Add($"{b}->{e.CorpseBody} (Corpse.def)");
        }
        Console.WriteLine($"bodies {ba.Length} vs {bb.Length}; entries that differ: {changed.Count}");
        foreach (var b in changed)
        {
            static string Seat(in AnimBodyEntry e) => e.MountSeat.Absolute ? $" seat{e.MountShift}" : e.MountShift != 0 ? $" shift{e.MountShift}" : "";
            string Desc(AnimBodyEntry[] t, int id) => id >= t.Length ? "absent" : t[id].HasFrames
                ? $"{((t[id].Source & AnimSource.Uop) != 0 ? "uop" : "anim" + ((t[id].Source & 0x7F) == 0 ? "" : ((t[id].Source & 0x7F) + 1).ToString()))}:{t[id].SourceBody} x{t[id].ActionCount} mh{t[id].MountHeight}{Seat(t[id])}"
                : t[id].AliasBody != AnimConst.NoBody ? $"alias {t[id].AliasBody} hue {t[id].AliasHue}, corpse {(t[id].CorpseBody == AnimConst.NoBody ? "none" : t[id].CorpseBody.ToString())}{Seat(t[id])}" : "nothing";
            Console.WriteLine($"  {b}: {Desc(ba, b)}  ->  {Desc(bb, b)}");
        }
        Console.WriteLine($"draw changed art through an alias: {viaAlias.Count}: {string.Join(", ", viaAlias)}");
        return 0;
    }

    public static int CompareIndexed(string idxPack, string b16Pack)
    {
        string id = Path.Combine(idxPack, "anim"), bd = Path.Combine(b16Pack, "anim");
        var files = Directory.GetFiles(id, "page_*.bin");
        long texels = 0, bad = 0, stray = 0, mipTexels = 0, mipBad = 0; int pagesBad = 0;
        Parallel.For(0, files.Length, ConvertOptions.Par, p =>
        {
            var il = Logical(PackConst.PagePath(id, p));
            var ih = MemoryMarshal.Read<PageHeader>(il);
            var bl = Logical(PackConst.PagePath(bd, p));
            var bh = MemoryMarshal.Read<PageHeader>(bl);
            int S = ih.Width;
            if (ih.Format != PixelFormat.Indexed8 || bh.Format != PixelFormat.B5G5R5A1 || bh.Width != S) throw new InvalidDataException($"page {p}: formats/sizes");
            var rec = IndexedPages.Reconstruct16(id, p, il.AsSpan(16, S * S), S, out long st);
            var b16 = MemoryMarshal.Cast<byte, ushort>(bl.AsSpan(16, S * S * 2));
            long nb = 0;
            for (int i = 0; i < rec.Length; i++) if (rec[i] != b16[i]) nb++;
            long mb = 0, mt = 0;
            var mp = AnimConst.MipsPath(id, p);
            if (File.Exists(mp))
            {
                var ml = Logical(mp);
                var rest = bl.AsSpan(16 + S * S * 2);
                var mine = ml.AsSpan(16);
                mt = rest.Length / 2;
                if (mine.Length != rest.Length) mb = mt;
                else for (int i = 0; i < rest.Length; i += 2) if (mine[i] != rest[i] || mine[i + 1] != rest[i + 1]) mb++;
            }
            Interlocked.Add(ref texels, rec.Length); Interlocked.Add(ref bad, nb); Interlocked.Add(ref stray, st);
            Interlocked.Add(ref mipTexels, mt); Interlocked.Add(ref mipBad, mb);
            if (nb + st + mb > 0) Interlocked.Increment(ref pagesBad);
        });
        var info = IndexedPages.Info(id);
        int palCount = info.Palettes.Length / AnimPaletteFile.Entries;
        var used = new int[palCount];
        for (int i = 0; i < palCount; i++)
        {
            int n = 0;
            for (int k = 1; k < AnimPaletteFile.Entries; k++) if (info.Palettes[i * AnimPaletteFile.Entries + k] != 0) n = k;
            used[i] = n;
        }
        Array.Sort(used);
        uint maxId = info.FramePalette.Where(x => x != uint.MaxValue).DefaultIfEmpty(0u).Max();
        Console.WriteLine($"compare {idxPack} vs {b16Pack}: {files.Length} pages, mip0 {texels} texels: {bad} mismatching, {stray} stray indices outside cells; " +
                          $"mips 1+ {mipTexels} texels: {mipBad} mismatching; {pagesBad} pages with differences -> {(bad + stray + mipBad == 0 ? "LOSSLESS" : "DIFFERENT")}");
        Console.WriteLine($"  palettes: {palCount} (max frame palette id {maxId}, fits u16: {maxId < 0xFFFF}); colours per palette median {used[used.Length / 2]}, p95 {used[(int)(used.Length * 0.95)]}, max {used[^1]}");
        return bad + stray + mipBad == 0 ? 0 : 2;
    }

    /// <summary>
    /// --filter-ref &lt;indexed pack&gt; &lt;b16 pack&gt; &lt;png dir&gt;: one frame of body 400 drawn at zoom 1.5 and 0.5 with hardware-style
    /// bilinear on the 16-bit page (mip0 at 1.5, mip1 at 0.5) and with the manual 4-tap filter on Indexed8 (palette lookup per
    /// tap, then blend). Both go through the renderer's alpha test (a &lt; 0.5 discarded) and un-premultiply.
    /// </summary>
    public static int FilterRef(string idxPack, string b16Pack, string outDir)
    {
        PublishedPacks.EnsureOutputDir(outDir); // writes PNGs only; never into a published pack
        Directory.CreateDirectory(outDir);
        string id = Path.Combine(idxPack, "anim"), bd = Path.Combine(b16Pack, "anim");
        var (bodies, dirs, frames) = AnimTableFile.Read(Path.Combine(id, AnimTableFile.FileName));
        var (bb, bdirs, bframes) = AnimTableFile.Read(Path.Combine(bd, AnimTableFile.FileName));
        var fr = AnimTableFile.Frames(bodies, dirs, frames, 400, 0, 4, out var de);
        var bfr = AnimTableFile.Frames(bb, bdirs, bframes, 400, 0, 4, out _);
        int fi = Math.Min(3, fr.Length - 1);
        var e = fr[fi].Sprite; var be = bfr[fi].Sprite;
        uint pal = IndexedPages.Info(id).FramePalette[de.FirstFrame + fi];
        var palette = IndexedPages.Info(id).Palettes.AsSpan((int)pal * 256, 256).ToArray();
        var il = Logical(PackConst.PagePath(id, e.Page));
        int S = MemoryMarshal.Read<PageHeader>(il).Width;
        var idx = il.AsSpan(16, S * S).ToArray();
        var b0 = PageCodec.ReadMip(PackConst.PagePath(bd, be.Page), 0, out _);
        var b1 = PageCodec.ReadMip(PackConst.PagePath(bd, be.Page), 1, out _);
        var i1 = File.Exists(AnimConst.MipsPath(id, e.Page)) ? PageCodec.ReadMip(PackConst.PagePath(id, e.Page), 1, out _) : null;
        var lines = new List<string>();
        foreach (var zoom in new[] { 1.5, 0.5 })
        {
            // The quad covers exactly the trimmed rect, as in the renderer; the filter footprint reaches at most half a
            // texel past it, i.e. into the 2 px gutter.
            int ow = (int)Math.Ceiling(e.W * zoom), oh = (int)Math.Ceiling(e.H * zoom);
            var hw = new uint[ow * oh]; var man = new uint[ow * oh]; var mipB = new uint[ow * oh]; var diff = new uint[ow * oh];
            int maxD = 0; long differ = 0;
            for (int oy = 0; oy < oh; oy++)
                for (int ox = 0; ox < ow; ox++)
                {
                    double tx = (ox + 0.5) / zoom, ty = (oy + 0.5) / zoom;
                    var h = zoom >= 1 ? Bilinear(b0, S, be.X + tx - 0.5, be.Y + ty - 0.5) : Bilinear(b1, S / 2, (be.X + tx) / 2 - 0.5, (be.Y + ty) / 2 - 0.5);
                    var m = Manual(idx, palette, S, e.X + tx - 0.5, e.Y + ty - 0.5);
                    var mb = zoom >= 1 || i1 == null ? m : Bilinear(i1, S / 2, (e.X + tx) / 2 - 0.5, (e.Y + ty) / 2 - 0.5);
                    uint ph = Shade(h), pm = Shade(m), pb = Shade(mb);
                    hw[oy * ow + ox] = ph; man[oy * ow + ox] = pm; mipB[oy * ow + ox] = pb;
                    uint dd = 0xFF000000;
                    for (int k = 0; k < 32; k += 8)
                    {
                        int d = Math.Abs((int)((ph >> k) & 0xFF) - (int)((pm >> k) & 0xFF));
                        maxD = Math.Max(maxD, d);
                        if (k < 24) dd |= (uint)Math.Min(255, d * 8) << k;
                    }
                    if (ph != pm) differ++;
                    diff[oy * ow + ox] = dd;
                }
            var panels = zoom >= 1 ? new[] { hw, man, diff } : new[] { hw, man, diff, mipB };
            const int K = 6, Gap = 8;
            int W = panels.Length * (ow * K + Gap), H = oh * K;
            var o = new uint[W * H];
            Array.Fill(o, 0xFFFFFFFF);
            for (int pi = 0; pi < panels.Length; pi++)
            {
                var src = pi == 2 ? panels[pi] : Png.OverChecker(panels[pi], ow, oh, 4);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < ow * K; x++)
                        o[y * W + pi * (ow * K + Gap) + x] = src[(y / K) * ow + x / K];
            }
            var name = $"indexed_zoom{(int)(zoom * 100):D3}.png";
            Png.Write(Path.Combine(outDir, name), W, H, o);
            lines.Add($"{name}: zoom {zoom}: hardware-style 16-bit ({(zoom >= 1 ? "mip0" : "mip1")}) | manual 4-tap Indexed8 mip0 | |diff| x8{(zoom >= 1 ? "" : " | Indexed8 + B5G5R5A1 mips (variant b)")}; " +
                      $"{differ}/{ow * oh} output pixels differ, max channel diff {maxD}");
        }
        foreach (var l in lines) Console.WriteLine(l);
        Console.WriteLine($"frame: body 400 walk dir 4 frame {fi}, {e.W}x{e.H}, page {e.Page}, palette {pal}");
        return 0;
    }

    private static (float R, float G, float B, float A) Texel(uint p) => ((p & 0xFF) / 255f, ((p >> 8) & 0xFF) / 255f, ((p >> 16) & 0xFF) / 255f, (p >> 24) / 255f);

    private static (float R, float G, float B, float A) Bilinear(uint[] px, int s, double u, double v)
    {
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        (float, float, float, float) T(int x, int y) => Texel(px[Math.Clamp(y, 0, s - 1) * s + Math.Clamp(x, 0, s - 1)]);
        return Blend(T(x0, y0), T(x0 + 1, y0), T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx, fy);
    }

    /// <summary>What the Indexed8 shader does: 4 point loads of the index, palette lookup per tap (index 0 = transparent), then the bilinear blend.</summary>
    private static (float R, float G, float B, float A) Manual(byte[] idx, ushort[] palette, int s, double u, double v)
    {
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        (float, float, float, float) T(int x, int y)
        {
            ushort c = palette[idx[Math.Clamp(y, 0, s - 1) * s + Math.Clamp(x, 0, s - 1)]];
            return (c & 0x8000) == 0 ? (0, 0, 0, 0) : Texel(ClassicUO.Utility.HuesHelper.Color16To32((ushort)(c & 0x7FFF)) | 0xFF000000);
        }
        return Blend(T(x0, y0), T(x0 + 1, y0), T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx, fy);
    }

    private static (float R, float G, float B, float A) Blend((float R, float G, float B, float A) a, (float R, float G, float B, float A) b,
        (float R, float G, float B, float A) c, (float R, float G, float B, float A) d, float fx, float fy)
    {
        float wa = (1 - fx) * (1 - fy), wb = fx * (1 - fy), wc = (1 - fx) * fy, wd = fx * fy;
        return (a.R * wa + b.R * wb + c.R * wc + d.R * wd, a.G * wa + b.G * wb + c.G * wc + d.G * wd,
                a.B * wa + b.B * wb + c.B * wc + d.B * wd, a.A * wa + b.A * wb + c.A * wc + d.A * wd);
    }

    /// <summary>The renderer's fragment rule: discard below alpha 0.5, un-premultiply, output opaque.</summary>
    private static uint Shade((float R, float G, float B, float A) c)
    {
        if (c.A < 0.5f) return 0;
        static uint B(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return B(c.R / c.A) | B(c.G / c.A) << 8 | B(c.B / c.A) << 16 | 0xFF000000;
    }
}
