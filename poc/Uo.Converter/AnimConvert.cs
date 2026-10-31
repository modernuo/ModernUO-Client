using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using Uo.Pack;
using LoaderDir = ClassicUO.Assets.AnimationsLoader.AnimationDirection;

namespace Uo.Converter;

/// <summary>
/// A private UOFileManager with a loaded AnimationsLoader, for index lookups and the serial verification decode
/// (frame decoding during conversion uses the managed port, AnimConvert.ReadFrames). Bodyconv.def is applied as the body
/// import choices resolve it (BodyPlan); a null plan installs no conversion at all (each body's own art).
/// </summary>
public sealed class AnimWorker : IDisposable
{
    public readonly UOFileManager Fm;
    public AnimationsLoader Loader => Fm.Animations;

    public AnimWorker(UOFileManager main, BodyPlan? plan)
    {
        Fm = new UOFileManager(main.Version, main.BasePath);
        Fm.Animations.Load();
        if (plan == null) return;
        Fm.Animations.SetBodyConv(plan.Conv);
        foreach (var (id, mt) in plan.MobTypes) Fm.Animations.SetMobType(id, mt.Type, mt.Flags);
    }

    public void Dispose() => Fm.Dispose();
}

public static class AnimConvert
{
    public sealed record Stats(int Bodies, int WithFrames, int Aliased, int UopBodies, int DirRecords, int UniqueDirs, int Frames, int Packed, int ContentDupes,
        int Pages, double Occupancy, long Bytes, long BytesBrotli, double DecodeSeconds, long InvalidMulBlocks, string Extra);

    /// <summary>Bodies scanned: ClassicUO's initial index size; raised to cover every id named in the def files.</summary>
    public const int MinBodyCount = 2048;

    /// <summary>--bodies: only these bodies get frames (the body table still covers every body).</summary>
    public static HashSet<int>? Bodies;

    /// <summary>What GetIndices returns for one body, plus the Bodyconv.def details it does not expose.</summary>
    public sealed class BodyMeta
    {
        public int Body;
        public bool Uop;
        public int FileIndex;
        public AnimType Type;
        public uint Flags;
        public LoaderDir[] Idx = [];
        public ushort SourceBody;
        public sbyte MountHeight;
        /// <summary>
        /// Converted MUL bodies: the body whose AnimationSequence.uop group replacements apply, which is the converted
        /// target (owner, 2026-10-31: daemon 9 drawn as anim2 body 7 follows 7's action list); -1 = none.
        /// </summary>
        public int SeqBody = -1;
    }

    /// <summary>A distinct block of client data: one stored direction of one action in one file.</summary>
    public readonly record struct DirKey(bool Uop, int FileIndex, uint Position, uint Size, int Dir, bool Equip);

    public sealed class DirJob
    {
        public DirKey Key;
        public BodyMeta Meta = null!;
        public int Action; // UOP: the replaced group; MUL: the action
        public int Dir;
    }

    private sealed class DecodedFrame
    {
        public short CenterX, CenterY, W, H;
        public SpriteImage? Img;
        public UInt128 Hash;
        // --anim-format indexed8: trimmed palette indices, the canonical palette (entry 0 = transparent) and its source.
        public byte[]? Indices;
        public ushort[]? Palette, SourcePalette;
        public bool PaletteOverflow;
    }

    private static IEnumerable<int> DefIds(UOFileManager fm, string name)
    {
        var p = fm.GetUOFilePath(name);
        if (!File.Exists(p)) yield break;
        foreach (var line in File.ReadLines(p))
        {
            var t = line.TrimStart();
            int n = 0;
            while (n < t.Length && char.IsDigit(t[n])) n++;
            if (n > 0 && int.TryParse(t.AsSpan(0, n), out var id)) yield return id;
        }
    }

    public static int BodyCount(UOFileManager fm)
    {
        int max = MinBodyCount - 1;
        foreach (var f in new[] { "mobtypes.txt", "Bodyconv.def", "Body.def", "Corpse.def" })
            foreach (var id in DefIds(fm, f)) if (id < 0x10000) max = Math.Max(max, id);
        return max + 1;
    }

    public static BodyMeta Meta(AnimationsLoader l, ClientVersion version, int body, BodyPlan plan) =>
        Meta(l, version, body, plan.Conv, plan.Choices.TargetSequences);

    /// <param name="targetSequence">bodies.json "sequences": "target": a converted MUL body follows its target's AnimationSequence.</param>
    public static BodyMeta Meta(AnimationsLoader l, ClientVersion version, int body, Dictionary<int, (int FileIndex, ushort Graphic, sbyte MountHeight)> conv, bool targetSequence)
    {
        ushort hue = 0;
        AnimationFlags flags = 0;
        var idx = l.GetIndices(version, (ushort)body, ref hue, ref flags, out int fileIndex, out var type);
        var m = new BodyMeta
        {
            Body = body, Uop = (flags & AnimationFlags.UseUopAnimation) != 0, FileIndex = fileIndex, Type = (AnimType)type,
            Flags = (uint)flags, Idx = idx.ToArray(), SourceBody = (ushort)body,
        };
        if (!m.Uop && conv.TryGetValue(body, out var c))
        {
            if (c.FileIndex != fileIndex) throw new InvalidOperationException($"body {body}: Bodyconv port says anim{c.FileIndex + 1}, loader says {fileIndex + 1}");
            m.SourceBody = c.Graphic;
            m.MountHeight = c.MountHeight;
            if (targetSequence && c.FileIndex > 0 && l.HasUopGroups(c.Graphic)) m.SeqBody = c.Graphic;
        }
        return m;
    }

    /// <summary>Stored data of (action, stored dir) for a body, as Animations.GetAnimationFrames picks it; null when there is none.</summary>
    public static DirJob? Job(AnimationsLoader l, BodyMeta m, int action, int dir)
    {
        if (m.Idx.Length == 0) return null;
        if (m.Uop)
        {
            byte g = (byte)action;
            l.ReplaceUopGroup((ushort)m.Body, ref g);
            if (g >= m.Idx.Length) return null;
            var e = m.Idx[g];
            if (e.Position == 0 && e.Size == 0) return null;
            return new DirJob { Key = new DirKey(true, m.FileIndex, e.Position, e.Size, dir, m.Type == AnimType.Equipment), Meta = m, Action = g, Dir = dir };
        }
        int group = SequenceGroup(l, m, action);
        int i = group * AnimationsLoader.MAX_DIRECTIONS + dir;
        if (i >= m.Idx.Length) return null;
        var d = m.Idx[i];
        if (d.Position == uint.MaxValue || d.Size == uint.MaxValue || d.Size == 0 || (d.Position == 0 && d.Size == 0)) return null;
        return new DirJob { Key = new DirKey(false, m.FileIndex, d.Position, d.Size, 0, false), Meta = m, Action = group, Dir = dir };
    }

    /// <summary>
    /// The stored MUL group drawn for <paramref name="action"/>: the converted target's AnimationSequence replacement,
    /// else the action. A replacement whose group has no frames in the drawn file is not applied: the sequence entries
    /// describe the target's UOP group layout, and some point past the MUL groups (giant spider 28 as anim2 5 would
    /// walk with group 22 of 22 and draw nothing).
    /// </summary>
    public static int SequenceGroup(AnimationsLoader l, BodyMeta m, int action)
    {
        if (m.SeqBody < 0 || action >= AnimationsLoader.MAX_ACTIONS) return action;
        byte g = (byte)action;
        l.ReplaceUopGroup((ushort)m.SeqBody, ref g);
        return g == action || MulGroupHasFrames(m, g) ? g : action;
    }

    public static bool MulGroupHasFrames(BodyMeta m, int group)
    {
        for (int d = 0; d < AnimationsLoader.MAX_DIRECTIONS; d++)
        {
            int i = group * AnimationsLoader.MAX_DIRECTIONS + d;
            if (i >= m.Idx.Length) return false;
            var e = m.Idx[i];
            if (!(e.Position == uint.MaxValue || e.Size == uint.MaxValue || e.Size == 0 || (e.Position == 0 && e.Size == 0))) return true;
        }
        return false;
    }

    /// <summary>
    /// Decodes one stored direction with the ClassicUO loader (the same calls Animations.GetAnimationFrames makes).
    /// The loader is not safe to call from several threads (see ReadFrames), so this is for serial use (verification).
    /// Blocks the port rejects as malformed are skipped, since the loader would crash or throw on them.
    /// </summary>
    public static Span<AnimationsLoader.FrameInfo> Read(AnimationsLoader l, DirJob j)
    {
        if (ReadFrames(l.FileManager, j) == null) return default;
        var m = j.Meta;
        if (m.Uop)
            return l.ReadUOPAnimationFrames((ushort)m.Body, (byte)j.Action, (byte)j.Dir, (AnimationGroupsType)m.Type, m.FileIndex, m.Idx[j.Action]);
        var e = m.Idx[j.Action * AnimationsLoader.MAX_DIRECTIONS + j.Dir];
        return l.ReadMULAnimationFrames(m.FileIndex, new LoaderDir { Position = e.Position, Size = e.Size });
    }

    /// <summary>Palette = the client's 256-entry 16-bit palette (MUL: per direction, shared; UOP: per frame); Uop = value 0 is transparent.</summary>
    public sealed record RawFrame(short CenterX, short CenterY, short W, short H, uint[]? Pixels, ushort[]? Palette = null, bool Uop = false);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool, int), Microsoft.Win32.SafeHandles.SafeFileHandle?> Handles = new();
    private static long _invalidBlocks;
    public static long InvalidBlocks => Interlocked.Read(ref _invalidBlocks);

    /// <summary>
    /// Port of ReadUOPAnimationFrames / ReadMULAnimationFrames + ReadSpriteData, with StackDataReader semantics (reads
    /// past the end return 0 and do not advance), so the output is bit-identical to the loader's. The loader itself
    /// cannot run on parallel workers: MUL reads take Unsafe.AsPointer of an unpinned array (a GC on another thread can
    /// move it mid-read), and concurrent calls into the native zlib crash. It also trusts the data, and a few Bodyconv
    /// targets in anim2/anim3 are malformed (offset tables past the end, runs outside the frame). Those return null and
    /// are treated as empty.
    /// </summary>
    public static RawFrame[]? ReadFrames(UOFileManager fm, DirJob j)
    {
        var m = j.Meta;
        var e = m.Uop ? m.Idx[j.Action] : m.Idx[j.Action * AnimationsLoader.MAX_DIRECTIONS + j.Dir];
        if (e.Position == 0 && e.Size == 0) return [];
        if (!m.Uop && (e.Position == uint.MaxValue || e.Size == uint.MaxValue || e.Size == 0)) return [];
        var fh = Handles.GetOrAdd((m.Uop, m.FileIndex), k =>
        {
            var p = k.Item1 ? fm.GetUOFilePath($"AnimationFrame{k.Item2 + 1}.uop") : fm.GetUOFilePath("anim" + (k.Item2 == 0 ? "" : (k.Item2 + 1).ToString()) + ".mul");
            return File.Exists(p) ? File.OpenHandle(p) : null;
        });
        if (fh == null || e.Position + (long)e.Size > RandomAccess.GetLength(fh)) return m.Uop ? Invalid() : [];
        var b = new byte[e.Size];
        RandomAccess.Read(fh, b, e.Position);
        try
        {
            return m.Uop ? ParseUop(b, e, j.Dir, m.Type) : ParseMul(b);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or DivideByZeroException)
        {
            return Invalid();
        }
    }

    private static RawFrame[]? Invalid() { Interlocked.Increment(ref _invalidBlocks); return null; }

    private static RawFrame[]? ParseMul(byte[] b)
    {
        if (b.Length < 516) return Invalid();
        var r = new Reader(b, 512);
        var palette = MemoryMarshal.Cast<byte, ushort>(b.AsSpan(0, 512)).ToArray();
        uint fc = r.U32();
        if (516 + 4L * fc > b.Length) return Invalid();
        var res = new RawFrame[fc];
        for (int i = 0; i < fc; i++)
        {
            long start = 512 + (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(516 + 4 * i));
            if (start > int.MaxValue) return Invalid();
            r.Pos = (int)start;
            var f = ReadSprite(ref r, palette, alphaCheck: false);
            if (f == null) return Invalid();
            res[i] = f;
        }
        return res;
    }

    private static RawFrame[]? ParseUop(byte[] b, LoaderDir e, int dir, AnimType type)
    {
        if (e.CompressionType >= CompressionType.Zlib)
        {
            var d = new byte[e.UncompressedSize];
            using (var z = new System.IO.Compression.ZLibStream(new MemoryStream(b), System.IO.Compression.CompressionMode.Decompress))
                if (z.ReadAtLeast(d, d.Length, throwOnEndOfStream: false) != d.Length) return Invalid();
            if (e.CompressionType == CompressionType.ZlibBwt) d = BwtDecompress.Decompress(d);
            b = d;
        }
        var r = new Reader(b, 32);
        int fc = (int)r.U32();
        r.Pos = (int)r.U32();
        var list = new List<(int Pos, uint PixelOffset, int FrameId)>();
        int last = 1;
        for (int i = 0; i < fc; i++)
        {
            int start = r.Pos;
            r.U16();
            int frameId = r.U16();
            r.U32(); r.U32();
            uint pixelOffset = r.U32();
            while (frameId - last > 1) list.Add((0, 0, ++last)); // missing frames stay empty
            list.Add((start, pixelOffset, frameId));
            last = frameId;
        }
        int real = (int)Math.Round(list.Count / (float)AnimationsLoader.MAX_DIRECTIONS);
        if (type == AnimType.Equipment) real = Math.Max(10, real);
        var res = new RawFrame[real];
        for (int i = 0; i < real; i++) res[i] = new RawFrame(0, 0, 0, 0, null);
        foreach (var f in list)
        {
            int fd = (f.FrameId - 1) / real;
            if (fd < dir) continue;
            if (fd > dir) break;
            int idx = (f.FrameId - 1) % real;
            if (f.Pos == 0) continue;
            r.Pos = (int)(f.Pos + f.PixelOffset);
            var palette = MemoryMarshal.Cast<byte, ushort>(b.AsSpan(r.Pos, 512)).ToArray();
            r.Pos += 512;
            res[idx] = ReadSprite(ref r, palette, alphaCheck: true) ?? throw new InvalidDataException();
        }
        return res;
    }

    private static RawFrame? ReadSprite(ref Reader r, ushort[] palette, bool alphaCheck)
    {
        short cx = (short)r.U16(), cy = (short)r.U16(), w = (short)r.U16(), h = (short)r.U16();
        if (w <= 0 || h <= 0) return new RawFrame(cx, cy, w, h, null, palette, alphaCheck);
        var px = new uint[w * h];
        uint header = r.U32();
        while (header != 0x7FFF7FFF && r.Pos < r.Len)
        {
            int run = (int)(header & 0x0FFF);
            int x = (int)((header >> 22) & 0x03FF), y = (int)((header >> 12) & 0x3FF);
            if ((x & 0x0200) > 0) x |= unchecked((int)0xFFFFFE00);
            if ((y & 0x0200) > 0) y |= unchecked((int)0xFFFFFE00);
            long block = (long)(y + cy + h) * w + x + cx;
            if (run > 0 && (block < 0 || block + run > px.Length)) return null;
            for (int k = 0; k < run; k++)
            {
                ushort v = palette[r.U8()];
                px[block + k] = !alphaCheck || v != 0 ? HuesHelper.Color16To32(v) | 0xFF000000 : 0;
            }
            int before = r.Pos;
            header = r.U32();
            if (r.Pos == before && r.Pos < r.Len) return null; // the loader would spin here forever
        }
        return new RawFrame(cx, cy, w, h, px, palette, alphaCheck);
    }

    /// <summary>StackDataReader semantics: reads past the end return 0 and leave the position unchanged.</summary>
    private struct Reader(byte[] b, int pos)
    {
        public int Pos = pos;
        public readonly int Len => b.Length;
        public byte U8() => Pos < 0 || Pos + 1 > b.Length ? (byte)0 : b[Pos++];
        public ushort U16() { if (Pos < 0 || Pos + 2 > b.Length) return 0; Pos += 2; return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(Pos - 2)); }
        public uint U32() { if (Pos < 0 || Pos + 4 > b.Length) return 0; Pos += 4; return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(Pos - 4)); }
    }

    // Body.def / Corpse.def chain, as Animations.GetAnimationFrames walks it (only while the body is a plain anim.mul body).
    private static (ushort Body, ushort Hue) Chain(AnimationsLoader l, int body, bool corpse, Func<int, BodyMeta> meta)
    {
        ushort id = (ushort)body, hue = 0;
        var m = meta(id);
        for (int guard = 0; !m.Uop && m.FileIndex == 0 && guard < 16; guard++)
        {
            bool replaced = corpse ? l.ReplaceCorpse(ref id, ref hue) : l.ReplaceBody(ref id, ref hue);
            if (!replaced) break;
            m = meta(id);
        }
        return id == body && hue == 0 ? (AnimConst.NoBody, (ushort)0) : (id, hue);
    }

    public static Stats Run(UOFileManager fm, string outDir, BodyPlan plan)
    {
        var dir = Path.Combine(outDir, "anim");
        // Stale pages from a previous run: delete the files written here (all *.bin, plus crashed-writer temps), never
        // the folder, which in a test pack may hold hard links from a published pack (deleting a link keeps the source).
        PackOutputFiles.DeleteFiles(dir, "*.bin");
        PackOutputFiles.DeleteFiles(dir, ReplacingFile.TempPattern);
        Directory.CreateDirectory(dir);
        int bodyCount = plan.BodyCount;
        var conv = plan.Conv;
        Console.WriteLine($"  bodies: {plan}");
        foreach (var line in plan.Describe()) Console.WriteLine($"  bodies: {line}");
        using var main = new AnimWorker(fm, plan);
        var l = main.Loader;
        var metas = new Dictionary<int, BodyMeta>();
        var standIns = plan.StandInIds.ToHashSet();
        BodyMeta MetaOf(int b) => metas.TryGetValue(b, out var m) ? m : metas[b] = Meta(l, fm.Version, b, plan);

        // Phase A (serial, cheap): body metadata and the distinct client blocks, in body/action/dir order.
        var bodies = new AnimBodyEntry[bodyCount];
        var bodyDirs = new int[bodyCount][];   // per body: (action * 5 + stored dir) -> unique dir job, -1 = none
        var jobs = new List<DirJob>();
        var jobOf = new Dictionary<DirKey, int>();
        int dirRecords = 0, withFrames = 0, aliased = 0, uopBodies = 0;
        for (int b = 0; b < bodyCount; b++)
        {
            var m = MetaOf(b);
            var (ab, ah) = Chain(l, b, false, MetaOf);
            var (cb, ch) = Chain(l, b, true, MetaOf);
            bodies[b] = new AnimBodyEntry
            {
                Type = m.Type, Source = AnimSource.None, Flags = m.Flags, SourceBody = m.SourceBody, MountHeight = m.MountHeight,
                AliasBody = ab, AliasHue = ah, CorpseBody = cb, CorpseHue = ch,
            };
            if (ab != AnimConst.NoBody) aliased++;
            if (Bodies != null && !Bodies.Contains(b)) continue;
            // An addition drawn as its Body.def stand-in: the client draws the alias only. Its anim.mul index, read in
            // the mobtypes layout, may point into other bodies' slots (318, 319, the mount ids), so store no frames.
            if (standIns.Contains(b)) continue;
            int actions = m.Uop ? AnimConst.MaxActions : m.Idx.Length / AnimationsLoader.MAX_DIRECTIONS;
            var map = new int[actions * AnimConst.StoredDirections];
            int last = -1;
            for (int a = 0; a < actions; a++)
                for (int d = 0; d < AnimConst.StoredDirections; d++)
                {
                    var j = Job(l, m, a, d);
                    int ji = -1;
                    if (j != null && !jobOf.TryGetValue(j.Key, out ji)) { ji = jobs.Count; jobOf[j.Key] = ji; jobs.Add(j); }
                    map[a * AnimConst.StoredDirections + d] = ji;
                    if (ji >= 0) last = a;
                }
            if (last < 0) continue;
            bodyDirs[b] = map;
            bodies[b].ActionCount = (ushort)(last + 1);
            bodies[b].Source = (byte)((m.Uop ? AnimSource.Uop : 0) | m.FileIndex);
            dirRecords += (last + 1) * AnimConst.Directions;
            withFrames++;
            if (m.Uop) uopBodies++;
        }

        ReportBodies(l, plan, metas, bodies);
        _usage = ConvertOptions.AnimIndexed ? PaletteUsage(fm, jobs) : null;

        // Phase B: decode distinct blocks in parallel batches; pack serially in job order (streaming atlas).
        var atlas = new StreamingAtlas(dir);
        var ranges = new (int First, int Count)[jobs.Count];
        var frames = new List<AnimFrameEntry>();
        var byHash = new Dictionary<UInt128, (SpriteEntry E, uint Pal)>();
        int packed = 0, dupes = 0;
        bool indexed = ConvertOptions.AnimIndexed;
        var framePal = new List<uint>();
        var palIds = new Dictionary<ushort[], uint>(PaletteComparer.Instance);
        var srcPalIds = new HashSet<ushort[]>(PaletteComparer.Instance);
        var palList = new List<ushort[]>();
        long srcPalInstances = 0, overflow = 0;
        var decodeSw = new System.Diagnostics.Stopwatch();
        int batch = ConvertOptions.Threads * 24;
        for (int start = 0; start < jobs.Count; start += batch)
        {
            int n = Math.Min(batch, jobs.Count - start);
            var decoded = new DecodedFrame[n][];
            decodeSw.Start();
            Parallel.For(0, n, ConvertOptions.Par, k => decoded[k] = Decode(fm, jobs[start + k]));
            decodeSw.Stop();
            for (int k = 0; k < n; k++)
            {
                ranges[start + k] = (frames.Count, decoded[k].Length);
                ushort[]? lastSrc = null;
                foreach (var f in decoded[k])
                {
                    var e = SpriteEntry.Absent;
                    uint pal = uint.MaxValue;
                    if (f.Img != null)
                    {
                        if (indexed && f.SourcePalette != lastSrc)
                        {
                            // MUL frames of one direction share one palette array; UOP frames each carry their own.
                            lastSrc = f.SourcePalette;
                            srcPalInstances++;
                            srcPalIds.Add(f.SourcePalette!);
                        }
                        if (byHash.TryGetValue(f.Hash, out var hit)) { e = hit.E; pal = hit.Pal; dupes++; }
                        else
                        {
                            if (indexed)
                            {
                                if (!palIds.TryGetValue(f.Palette!, out pal)) { pal = (uint)palList.Count; palIds[f.Palette!] = pal; palList.Add(f.Palette!); }
                                if (f.PaletteOverflow) overflow++;
                            }
                            e = atlas.Add(f.Img, f.Indices, f.Palette);
                            byHash[f.Hash] = (e, pal);
                            packed++;
                        }
                    }
                    framePal.Add(pal);
                    e.OrigW = (ushort)Math.Max((short)0, f.W);
                    e.OrigH = (ushort)Math.Max((short)0, f.H);
                    frames.Add(new AnimFrameEntry { Sprite = e, CenterX = f.CenterX, CenterY = f.CenterY });
                }
            }
            atlas.FlushClosed();
        }
        atlas.Finish();

        // Phase C: dir records (8 per action; the 3 mirrored directions point at their stored direction's frames).
        var dirs = new AnimDirEntry[dirRecords];
        int next = 0;
        for (int b = 0; b < bodyCount; b++)
        {
            if (bodyDirs[b] == null) continue;
            bodies[b].FirstDir = (uint)next;
            for (int a = 0; a < bodies[b].ActionCount; a++)
                for (int d = 0; d < AnimConst.Directions; d++)
                {
                    var (sd, mirrored) = AnimConst.MapDirection(d);
                    int ji = bodyDirs[b][a * AnimConst.StoredDirections + sd];
                    var r = ji >= 0 ? ranges[ji] : (0, 0);
                    dirs[next++] = new AnimDirEntry
                    {
                        FirstFrame = (uint)r.Item1, FrameCount = (ushort)r.Item2, StoredDir = sd, Flags = mirrored ? AnimDirFlags.Mirrored : 0,
                    };
                }
        }
        var frameArr = frames.ToArray();
        AnimTableFile.Write(Path.Combine(dir, AnimTableFile.FileName), bodies, dirs, frameArr);
        AnimDataFile.Write(Path.Combine(dir, AnimDataFile.FileName), AnimData(fm));
        string extra = $"page {atlas.Size}px x{atlas.Mips} mips, sprite texels / page texels {atlas.SpriteFill:P1}";
        if (indexed)
        {
            var flat = new ushort[palList.Count * AnimPaletteFile.Entries];
            for (int i = 0; i < palList.Count; i++) palList[i].CopyTo(flat, i * AnimPaletteFile.Entries);
            var palPath = Path.Combine(dir, AnimPaletteFile.FileName);
            AnimPaletteFile.Write(palPath, framePal.ToArray(), flat);
            extra += $"; indexed8: source palettes {srcPalInstances} ({srcPalIds.Count} distinct by content), canonical after dedupe {palList.Count}, overflow (>255 colours) {overflow}, " +
                     $"palettes.bin {new FileInfo(palPath).Length} B ({flat.Length * 2} B palettes); mips sidecar raw {atlas.MipBytes} B, file {atlas.MipBytesBrotli} B; " +
                     $"self-check vs B5G5R5A1 mip0: {atlas.IndexedMismatch} mismatching texels";
        }
        return new Stats(bodyCount, withFrames, aliased, uopBodies, dirRecords, jobs.Count, frameArr.Length, packed, dupes,
            atlas.PageCount, atlas.Occupancy, atlas.PageBytes, atlas.PageBytesBrotli, decodeSw.Elapsed.TotalSeconds, InvalidBlocks, extra);
    }

    /// <summary>Console notes: converted bodies whose drawn groups follow their target's AnimationSequence, and a ModernUO bodyTable.cfg line per extra.</summary>
    private static void ReportBodies(AnimationsLoader l, BodyPlan plan, Dictionary<int, BodyMeta> metas, AnimBodyEntry[] bodies)
    {
        var seq = new List<string>();
        int replaced = 0, hidden = 0, skipped = 0;
        foreach (var (b, m) in metas.OrderBy(kv => kv.Key))
        {
            if (m.SeqBody < 0 || m.Uop) continue;
            int actions = m.Idx.Length / AnimationsLoader.MAX_DIRECTIONS;
            var diffs = new List<string>();
            for (int a = 0; a < actions; a++)
            {
                int g = SequenceGroup(l, m, a);
                if (g == a)
                {
                    byte raw = (byte)a;
                    l.ReplaceUopGroup((ushort)m.SeqBody, ref raw);
                    if (raw != a) { skipped++; diffs.Add($"{a}->{raw}(empty, kept {a})"); }
                    continue;
                }
                // '*': the stored group a has frames of its own, which the replacement hides
                bool own = MulGroupHasFrames(m, a);
                replaced++;
                if (own) hidden++;
                diffs.Add($"{a}->{g}{(own ? "*" : "")}");
            }
            if (diffs.Count > 0) seq.Add($"{b} (anim{m.FileIndex + 1}:{m.SourceBody}: {string.Join(" ", diffs)})");
        }
        if (plan.Choices.TargetSequences)
            Console.WriteLine($"  bodies: {seq.Count} converted bodies draw groups per their target's AnimationSequence ({replaced} replaced groups, {hidden} of them (*) had frames of their own; {skipped} replacements to an empty group not applied): {string.Join(", ", seq)}");
        foreach (var x in plan.Choices.Extras)
        {
            var kind = bodies[x.Id].Type switch
            {
                AnimType.Monster => "Monster", AnimType.SeaMonster => "Sea", AnimType.Animal => "Animal", AnimType.Human => "Human",
                AnimType.Equipment => "Equipment", _ => "Empty",
            };
            Console.WriteLine($"  extra {x.Id} = {x.From} (type {bodies[x.Id].Type}, flags 0x{bodies[x.Id].Flags:X8}, mount height {bodies[x.Id].MountHeight}); ModernUO Data/bodyTable.cfg line: {x.Id}	{kind}");
        }
    }

    private static DecodedFrame[] Decode(UOFileManager fm, DirJob j)
    {
        var src = ReadFrames(fm, j) ?? [];
        var res = new DecodedFrame[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            res[i] = Frame(src[i].CenterX, src[i].CenterY, src[i].W, src[i].H, src[i].Pixels);
            if (ConvertOptions.AnimIndexed && res[i].Img != null) Index(res[i], src[i].Palette!, src[i].Uop, _usage![src[i].Palette!]);
        }
        return res;
    }

    [ThreadStatic] private static byte[]? _lut;
    private static Dictionary<ushort[], ulong[]>? _usage;

    /// <summary>
    /// Indexed8 pre-pass: for every distinct source palette (by content), the set of 15-bit colours any frame drawn with
    /// it actually uses. MUL palettes often hold 256 distinct colours and no transparent entry; dropping colours no
    /// frame uses frees index 0 for transparency while keeping one canonical palette per source palette.
    /// </summary>
    private static Dictionary<ushort[], ulong[]> PaletteUsage(UOFileManager fm, List<DirJob> jobs)
    {
        var map = new Dictionary<ushort[], ulong[]>(PaletteComparer.Instance);
        Parallel.For(0, jobs.Count, ConvertOptions.Par, k =>
        {
            var local = new Dictionary<ushort[], ulong[]>(ReferenceEqualityComparer.Instance);
            foreach (var f in ReadFrames(fm, jobs[k]) ?? [])
            {
                if (f.Palette == null) continue;
                if (!local.TryGetValue(f.Palette, out var bits)) local[f.Palette] = bits = new ulong[0x8000 / 64];
                if (f.Pixels == null) continue;
                foreach (var p in f.Pixels)
                    if ((p >> 24) != 0) { int c = To16(p); bits[c >> 6] |= 1UL << (c & 63); }
            }
            lock (map)
                foreach (var (pal, bits) in local)
                {
                    if (!map.TryGetValue(pal, out var all)) { map[pal] = bits; continue; }
                    for (int i = 0; i < all.Length; i++) all[i] |= bits[i];
                }
        });
        return map;
    }

    /// <summary>
    /// Canonical palette: entry 0 = transparent (0), then the source palette's distinct visible B5G5R5A1 colours that some
    /// frame drawn with that palette uses (see PaletteUsage), in order of first appearance (UOP value 0 is transparent and
    /// skipped). If that still exceeds 255 colours, the frame's own used colours are used instead.
    /// </summary>
    private static void Index(DecodedFrame d, ushort[] src, bool uop, ulong[] used)
    {
        var lut = _lut ??= new byte[0x8000];
        var img = d.Img!;
        var pal = new ushort[AnimPaletteFile.Entries];
        int n = 1;
        foreach (var v in src)
        {
            if (uop && v == 0) continue;
            int c = v & 0x7FFF;
            if (lut[c] != 0 || (used[c >> 6] & (1UL << (c & 63))) == 0) continue;
            if (n == 256) { n = -1; break; }
            lut[c] = (byte)n;
            pal[n++] = (ushort)(c | 0x8000);
        }
        if (n < 0)
        {
            for (int i = 1; i < 256; i++) lut[pal[i] & 0x7FFF] = 0;
            Array.Clear(pal);
            n = 1;
            d.PaletteOverflow = true;
            foreach (var p in img.Pixels)
            {
                if ((p >> 24) == 0) continue;
                int c = To16(p);
                if (lut[c] != 0) continue;
                if (n == 256) throw new InvalidOperationException("frame uses more than 255 colours");
                lut[c] = (byte)n;
                pal[n++] = (ushort)(c | 0x8000);
            }
        }
        var idx = new byte[img.Pixels.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            uint p = img.Pixels[i];
            if ((p >> 24) == 0) continue;
            byte b = lut[To16(p)];
            if (b == 0) throw new InvalidOperationException("pixel colour missing from its palette");
            idx[i] = b;
        }
        for (int i = 1; i < n; i++) lut[pal[i] & 0x7FFF] = 0;
        d.Indices = idx;
        d.Palette = pal;
        d.SourcePalette = src;
    }

    /// <summary>RGBA8 (ClassicUO-expanded) -> 15-bit colour; exact inverse of HuesHelper.Color16To32.</summary>
    public static int To16(uint p) => (int)(((((p >> 16) & 0xFF) * 31 + 127) / 255) | ((((p >> 8) & 0xFF) * 31 + 127) / 255) << 5 | (((p & 0xFF) * 31 + 127) / 255) << 10);

    private static DecodedFrame Frame(short cx, short cy, short w, short h, ReadOnlySpan<uint> px)
    {
        var d = new DecodedFrame { CenterX = cx, CenterY = cy, W = w, H = h };
        if (w <= 0 || h <= 0 || px.IsEmpty) return d;
        d.Img = SpriteImage.Trim(0, px, w, h, SpriteFlags.None);
        if (d.Img == null) return d;
        Span<byte> hash = stackalloc byte[16];
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(MemoryMarshal.AsBytes(d.Img.Pixels.AsSpan()));
        md5.AppendData(MemoryMarshal.AsBytes(stackalloc int[] { d.Img.W, d.Img.H, d.Img.TrimX, d.Img.TrimY }));
        md5.GetHashAndReset(hash);
        d.Hash = MemoryMarshal.Read<UInt128>(hash);
        return d;
    }

    /// <summary>animdata.mul records of every static graphic that has frames.</summary>
    public static AnimDataEntry[] AnimData(UOFileManager fm)
    {
        var path = fm.GetUOFilePath("animdata.mul");
        if (!File.Exists(path)) return [];
        var bytes = File.ReadAllBytes(path);
        var list = new List<AnimDataEntry>();
        for (int g = 0; g < 0x10000; g++)
        {
            int pos = g * 68 + 4 * ((g >> 3) + 1); // AnimDataLoader.CalculateCurrentGraphic
            if (pos + 68 > bytes.Length) break;
            var e = new AnimDataEntry { Graphic = (ushort)g, Unknown = bytes[pos + 64], FrameCount = bytes[pos + 65], FrameInterval = bytes[pos + 66], FrameStart = bytes[pos + 67] };
            if (e.FrameCount == 0) continue;
            for (int i = 0; i < 64; i++) e.Frames[i] = (sbyte)bytes[pos + i];
            list.Add(e);
        }
        return list.ToArray();
    }
}

/// <summary>Content equality for 256-entry palettes.</summary>
public sealed class PaletteComparer : IEqualityComparer<ushort[]>
{
    public static readonly PaletteComparer Instance = new();
    public bool Equals(ushort[]? a, ushort[]? b) => a.AsSpan().SequenceEqual(b);
    public int GetHashCode(ushort[] p)
    {
        var h = new HashCode();
        h.AddBytes(MemoryMarshal.AsBytes(p.AsSpan()));
        return h.ToHashCode();
    }
}

/// <summary>
/// AtlasBuilder's packing (skyline, sequential, previous page kept open for small sprites) without holding every
/// sprite in memory: once a page can no longer receive sprites it is encoded, written, and its pixels released.
/// Page files are identical in format to AtlasBuilder's. Page side is --anim-page-size. With --anim-format indexed8,
/// mip0 is written as Indexed8 and mips 1+ (optionally) as a half-size B5G5R5A1 sidecar page.
/// </summary>
public sealed class StreamingAtlas
{
    private const int G = PackConst.Gutter;
    private readonly int S = ConvertOptions.AnimPageSize, _mips = ConvertOptions.MipsFor(ConvertOptions.AnimPageSize);
    private readonly string _dir;
    private readonly int _align = ConvertOptions.AnimIndexed ? 1 : ConvertOptions.Align;
    private readonly List<SkylinePacker> _packers = new();
    private readonly List<List<(SpriteImage Img, SpriteEntry E, byte[]? Idx, ushort[]? Pal)>?> _pages = new();
    private readonly List<int> _closed = new();
    private int _cur, _prev = -1;
    private long _used, _spriteArea;
    public long PageBytes, PageBytesBrotli, MipBytes, MipBytesBrotli, IndexedMismatch;
    public int PageCount => _pages.Count;
    public int Size => S;
    public int Mips => _mips;
    public double Occupancy => _pages.Count == 0 ? 0 : _used / (double)((long)_pages.Count * S * S);
    public double SpriteFill => _pages.Count == 0 ? 0 : _spriteArea / (double)((long)_pages.Count * S * S);

    public StreamingAtlas(string dir) { _dir = dir; _cur = Open(); }

    private int Open() { _packers.Add(new SkylinePacker(S, S)); _pages.Add(new()); return _packers.Count - 1; }
    private int Cell(int v) => (v + 2 * G + _align - 1) / _align * _align;

    public SpriteEntry Add(SpriteImage sp, byte[]? indices = null, ushort[]? palette = null)
    {
        int pw = Cell(sp.W), ph = Cell(sp.H);
        if (pw > S || ph > S) throw new InvalidOperationException($"frame {sp.W}x{sp.H} too large for a page");
        int page, x, y;
        if (_packers[_cur].TryInsert(pw, ph, out x, out y)) page = _cur;
        else if (_prev >= 0 && _packers[_prev].TryInsert(pw, ph, out x, out y)) page = _prev;
        else
        {
            if (_prev >= 0) _closed.Add(_prev);
            _prev = _cur;
            _cur = Open();
            if (!_packers[_cur].TryInsert(pw, ph, out x, out y)) throw new InvalidOperationException("cannot pack");
            page = _cur;
        }
        var e = new SpriteEntry
        {
            Page = (ushort)page, X = (ushort)(x + G), Y = (ushort)(y + G), W = (ushort)sp.W, H = (ushort)sp.H,
            TrimX = (short)sp.TrimX, TrimY = (short)sp.TrimY, OrigW = (ushort)sp.OrigW, OrigH = (ushort)sp.OrigH, Flags = sp.Flags,
        };
        _pages[page]!.Add((sp, e, indices, palette));
        _spriteArea += (long)sp.W * sp.H;
        return e;
    }

    public void FlushClosed()
    {
        if (_closed.Count == 0) return;
        var todo = _closed.ToArray();
        _closed.Clear();
        int dop = ConvertOptions.Format == PixelFormat.Bc7 ? 1 : ConvertOptions.Threads;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = dop }, Write);
        foreach (var p in todo) { _used += _packers[p].UsedArea; _pages[p] = null; }
    }

    public void Finish()
    {
        if (_prev >= 0) _closed.Add(_prev);
        if (_pages[_cur]!.Count > 0 || _cur == 0) _closed.Add(_cur);
        else { _pages.RemoveAt(_cur); _packers.RemoveAt(_cur); }
        FlushClosed();
    }

    private void Write(int p)
    {
        var format = ConvertOptions.Format;
        var chain = GC.AllocateUninitializedArray<byte>(AtlasPage.ChainBytes(S, _mips));
        var mip0 = MemoryMarshal.Cast<byte, uint>(chain.AsSpan(0, AtlasPage.MipBytes(S, 0)));
        mip0.Clear();
        foreach (var (sp, e, _, _) in _pages[p]!)
            AtlasBuilder.Blit(mip0, S, e.X, e.Y, sp.Pixels, sp.W, sp.H, G, Cell(sp.W) - sp.W - G, Cell(sp.H) - sp.H - G);
        AtlasBuilder.BuildMips(chain, S, _mips);
        var path = PackConst.PagePath(_dir, p);
        if (ConvertOptions.AnimIndexed)
        {
            WriteIndexed(p, chain, path);
            return;
        }
        byte[] data = format switch
        {
            PixelFormat.B5G5R5A1 => PageCodec.EncodeB5G5R5A1(chain, S, _mips),
            PixelFormat.Bc7 => PageCodec.EncodeBc7(chain, S, _mips),
            _ => chain,
        };
        AtlasPage.Write(path, S, _mips, format, data, ConvertOptions.PageKind, ConvertOptions.ChunkBytes);
        Interlocked.Add(ref PageBytes, data.Length + 32);
        Interlocked.Add(ref PageBytesBrotli, new FileInfo(path).Length);
    }

    private void WriteIndexed(int p, byte[] chain, string path)
    {
        var idx = new byte[S * S];
        foreach (var (sp, e, ix, _) in _pages[p]!)
            AtlasBuilder.Blit<byte>(idx, S, e.X, e.Y, ix, sp.W, sp.H, G, G, G);
        // Self-check: palette[index] per cell must equal the B5G5R5A1 encoding of the same page's mip0, gutters included.
        var b16 = MemoryMarshal.Cast<byte, ushort>(PageCodec.EncodeB5G5R5A1(chain.AsSpan(0, AtlasPage.MipBytes(S, 0)), S, 1).AsSpan());
        var rec = new ushort[S * S];
        foreach (var (sp, e, _, pal) in _pages[p]!)
            for (int y = e.Y - G; y < e.Y + e.H + G; y++)
                for (int x = e.X - G; x < e.X + e.W + G; x++)
                    rec[y * S + x] = pal![idx[y * S + x]];
        long bad = 0;
        for (int i = 0; i < rec.Length; i++) if (rec[i] != b16[i]) bad++;
        if (bad != 0) Interlocked.Add(ref IndexedMismatch, bad);
        AtlasPage.Write(path, S, 1, PixelFormat.Indexed8, idx, ConvertOptions.PageKind, ConvertOptions.ChunkBytes);
        Interlocked.Add(ref PageBytes, idx.Length + 32);
        Interlocked.Add(ref PageBytesBrotli, new FileInfo(path).Length);
        if (!ConvertOptions.IndexedMips || _mips < 2) return;
        var mips = PageCodec.EncodeB5G5R5A1(chain.AsSpan(AtlasPage.MipBytes(S, 0)), S / 2, _mips - 1);
        var mp = AnimConst.MipsPath(_dir, p);
        AtlasPage.Write(mp, S / 2, _mips - 1, PixelFormat.B5G5R5A1, mips, ConvertOptions.PageKind, ConvertOptions.ChunkBytes);
        Interlocked.Add(ref MipBytes, mips.Length + 32);
        Interlocked.Add(ref MipBytesBrotli, new FileInfo(mp).Length);
    }
}
