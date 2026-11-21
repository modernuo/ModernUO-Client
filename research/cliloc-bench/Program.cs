// shared-tables: cliloc lookup latency for (a) the heap path (today), (b) a shared mapping read through a span over the
// base pointer acquired once (shared_cliloc), (c) a MemoryMappedViewAccessor (reference only), and (d) the mapping with
// UTF-8 decoded on every lookup instead of a string cache. Cold = the first lookups after a fresh load (first touch:
// page faults and string creation); warm = the same stream again.
//
//   cliloc-bench run <cliloc.bin> <heap|map|accessor|map-nocache> [--ids <trace.i32>] [--prefault] [--lang enu]
//   cliloc-bench hold <cliloc.bin> <seconds>      maps and touches the table, then waits (the "second instance")
//
// Output: one "RESULT key=value ..." line per (stream, phase), for the job script to collect.

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using ClassicUO.Assets;
using Uo.Pack;

internal static unsafe class Program
{
    private interface ILookup
    {
        bool TryGet(int id, out string text);
    }

    private sealed class TableLookup(ClilocTable t) : ILookup
    {
        public bool TryGet(int id, out string text) => t.TryGetString(id, out text);
    }

    // the mapping without a string cache: UTF-8 decoded at every lookup
    private sealed class NoCacheLookup(ClilocTable t) : ILookup
    {
        public bool TryGet(int id, out string text)
        {
            int i = t.IndexOf(id);
            text = i < 0 ? null : Encoding.UTF8.GetString(t.Utf8At(i));
            return i >= 0;
        }
    }

    // the mapping with a flat string cache (one slot per id, like the heap table) instead of blocks
    private sealed class FlatCacheLookup(ClilocTable t) : ILookup
    {
        private readonly string[] _cache = new string[t.Count];

        public bool TryGet(int id, out string text)
        {
            int i = t.IndexOf(id);
            if (i < 0) { text = null; return false; }
            text = _cache[i] ??= Encoding.UTF8.GetString(t.Utf8At(i));
            return true;
        }
    }

    // reference only: every read through the accessor's bounds-checked, SafeBuffer-acquiring calls
    private sealed class AccessorLookup : ILookup
    {
        private readonly MemoryMappedViewAccessor _v;
        private readonly int _count;
        private readonly long _blobAt;
        private readonly string[] _cache;
        private byte[] _buf = new byte[4096];

        public AccessorLookup(MemoryMappedViewAccessor v)
        {
            _v = v;
            _count = v.ReadInt32(0);
            _blobAt = 8 + (long)_count * 8;
            _cache = new string[_count];
        }

        public bool TryGet(int id, out string text)
        {
            int lo = 0, hi = _count - 1;

            while (lo <= hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                int v = _v.ReadInt32(8 + mid * 4L);

                if (v == id)
                {
                    text = _cache[mid] ??= Decode(mid);
                    return true;
                }

                if (v < id) lo = mid + 1;
                else hi = mid - 1;
            }

            text = null;
            return false;
        }

        private string Decode(int i)
        {
            long endsAt = 8 + (long)_count * 4;
            uint start = i == 0 ? 0 : _v.ReadUInt32(endsAt + (i - 1) * 4L);
            int len = (int)(_v.ReadUInt32(endsAt + i * 4L) - start);
            if (_buf.Length < len) _buf = new byte[len];
            _v.ReadArray(_blobAt + start, _buf, 0, len);
            return Encoding.UTF8.GetString(_buf, 0, len);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(nint process, out ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    private static (long Faults, long WorkingSet) Mem()
    {
        if (!OperatingSystem.IsWindows()) return (0, Environment.WorkingSet);
        GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)sizeof(ProcessMemoryCounters));
        return (c.PageFaultCount, (long)c.WorkingSetSize);
    }

    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "hold") return Hold(args[1], int.Parse(args[2]));
        if (args.Length < 3 || args[0] != "run")
        {
            Console.Error.WriteLine("usage: cliloc-bench run <cliloc.bin> <heap|map|accessor|map-nocache> [--ids f] [--prefault] [--lang enu] | hold <cliloc.bin> <s>");
            return 2;
        }

        string path = args[1], mode = args[2], idsFile = null, lang = "enu";
        bool prefault = false;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--ids") idsFile = args[++i];
            else if (args[i] == "--prefault") prefault = true;
            else if (args[i] == "--lang") lang = args[++i];
        }

        var (rec, fileBytes) = FindRecord(path, lang);
        // the id streams come from a heap copy that is dropped before the measured load
        int[] allIds, tooltip;
        {
            using var store = RecordStore.Open(path, TextConst.MagicCliloc);
            var t = ClilocStore.ReadTable(store, rec.Index, out _);
            allIds = new int[t.Count];
            for (int i = 0; i < t.Count; i++) allIds[i] = t.IdAt(i);
        }
        tooltip = idsFile != null ? TraceIds(idsFile) : SyntheticTooltip(allIds);
        var random = new int[20000];
        var rng = new Random(12345);
        for (int i = 0; i < random.Length; i++) random[i] = allIds[rng.Next(allIds.Length)];
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        var m0 = Mem();
        long t0 = Stopwatch.GetTimestamp();
        ILookup lookup;
        SharedTableCache cache = null;
        MemoryMappedFile mmf = null;
        MemoryMappedViewAccessor view = null;
        switch (mode)
        {
            case "heap":
            {
                using var store = RecordStore.Open(path, TextConst.MagicCliloc);
                lookup = new TableLookup(ClilocStore.ReadTable(store, rec.Index, out _));
                break;
            }
            case "map":
            case "map-nocache":
            case "map-flat":
            case "heap-flat":
            {
                ClilocTable t;
                if (mode == "heap-flat")
                {
                    using var store = RecordStore.Open(path, TextConst.MagicCliloc);
                    t = ClilocStore.ReadTable(store, rec.Index, out _);
                }
                else
                {
                    cache = new SharedTableCache(Path.Combine(Path.GetTempPath(), "cliloc-bench-unused"), "x");
                    var m = cache.MapFileRange(path, (long)rec.Offset, rec.RawBytes);
                    t = ClilocTable.TryParseMapped(m.Pointer, (int)rec.RawBytes, m);
                    if (prefault) m.Prefault();
                }
                lookup = mode == "map" ? new TableLookup(t) : mode == "map-nocache" ? new NoCacheLookup(t) : new FlatCacheLookup(t);
                break;
            }
            case "accessor":
                mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                view = mmf.CreateViewAccessor((long)rec.Offset, rec.RawBytes, MemoryMappedFileAccess.Read);
                lookup = new AccessorLookup(view);
                break;
            default:
                Console.Error.WriteLine($"unknown mode {mode}");
                return 2;
        }
        double loadMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var m1 = Mem();
        Console.WriteLine($"RESULT mode={mode} phase=load ms={loadMs:0.000} faults={m1.Faults - m0.Faults} wsDeltaKB={(m1.WorkingSet - m0.WorkingSet) / 1024} " +
                          $"recordBytes={rec.RawBytes} ids={allIds.Length} tooltipIds={tooltip.Length} prefault={prefault}");

        Measure(mode, "tooltip", lookup, tooltip);
        Measure(mode, "random", lookup, random);
        GC.KeepAlive(cache);
        view?.Dispose();
        mmf?.Dispose();
        return 0;
    }

    private static void Measure(string mode, string stream, ILookup lookup, int[] ids)
    {
        // cold: the stream's first pass, each call timed (first touch: page faults, string creation)
        int coldN = Math.Min(ids.Length, 4000);
        var cold = new double[coldN];
        double tick = 1e9 / Stopwatch.Frequency;
        var f0 = Mem();
        int sink = 0;
        for (int i = 0; i < coldN; i++)
        {
            long a = Stopwatch.GetTimestamp();
            lookup.TryGet(ids[i], out string s);
            long b = Stopwatch.GetTimestamp();
            cold[i] = (b - a) * tick;
            sink += s?.Length ?? 0;
        }
        var f1 = Mem();
        for (int i = coldN; i < ids.Length; i++) { lookup.TryGet(ids[i], out string s); sink += s?.Length ?? 0; }

        // warm: batches of 256 lookups, per-lookup time = batch / 256 (Stopwatch resolution is ~100 ns)
        const int Batch = 256;
        int rounds = Math.Max(1, 2_000_000 / ids.Length);
        var warm = new List<double>();
        long allocs0 = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 0; r < rounds; r++)
        {
            for (int i = 0; i + Batch <= ids.Length; i += Batch)
            {
                long a = Stopwatch.GetTimestamp();
                for (int k = 0; k < Batch; k++) { lookup.TryGet(ids[i + k], out string s); sink += s?.Length ?? 0; }
                warm.Add((Stopwatch.GetTimestamp() - a) * tick / Batch);
            }
        }
        long allocs = GC.GetAllocatedBytesForCurrentThread() - allocs0;
        var f2 = Mem();
        var w = warm.ToArray();
        Console.WriteLine($"RESULT mode={mode} stream={stream} phase=cold n={coldN} p50ns={P(cold, 50):0} p90ns={P(cold, 90):0} p99ns={P(cold, 99):0} maxns={cold.Max():0} meanns={cold.Average():0} faults={f1.Faults - f0.Faults}");
        Console.WriteLine($"RESULT mode={mode} stream={stream} phase=warm batches={w.Length} p50ns={P(w, 50):0.0} p99ns={P(w, 99):0.0} meanns={w.Average():0.0} faults={f2.Faults - f1.Faults} allocBytes={allocs} sink={sink}");
    }

    private static double P(double[] v, double pct)
    {
        var s = (double[])v.Clone();
        Array.Sort(s);
        return s[Math.Min(s.Length - 1, (int)(pct / 100 * s.Length))];
    }

    private static int Hold(string path, int seconds)
    {
        var (rec, _) = FindRecord(path, "enu");
        var cache = new SharedTableCache(Path.Combine(Path.GetTempPath(), "cliloc-bench-unused"), "x");
        var m = cache.MapFileRange(path, (long)rec.Offset, rec.RawBytes);
        int sum = m.Prefault();
        Console.WriteLine($"HOLD mapped {rec.RawBytes} bytes, touched (sum {sum}); waiting {seconds} s");
        Console.Out.Flush();
        Thread.Sleep(seconds * 1000);
        GC.KeepAlive(cache);
        return 0;
    }

    private static ((int Index, ulong Offset, uint RawBytes) Rec, long FileBytes) FindRecord(string path, string lang)
    {
        using var store = RecordStore.Open(path, TextConst.MagicCliloc);
        var dir = ClilocStore.Resolve(store);
        int rec = dir.Languages[lang];
        ref readonly var r = ref store.Records[rec];
        if (r.Codec != StoreConst.CodecRaw) throw new InvalidOperationException("the bench maps raw records only (use a ship-profile pack)");
        return ((rec, r.Offset, r.RawBytes), store.FileLength);
    }

    private static int[] TraceIds(string file)
    {
        var b = File.ReadAllBytes(file);
        return MemoryMarshal.Cast<byte, int>(b.AsSpan(0, b.Length & ~3)).ToArray();
    }

    // Without a capture: a Zipf-like stream (s = 1.1) over 600 ids from the ranges object property lists use (item names
    // 1020000-1030000, property lines 1060000-1062000 and 1070000-1080000, 1041000-1042000, 1150000-1160000).
    private static int[] SyntheticTooltip(int[] all)
    {
        var rng = new Random(7);
        var pool = all.Where(id => id is >= 1020000 and < 1030000 or >= 1060000 and < 1062000 or >= 1070000 and < 1080000
            or >= 1041000 and < 1042000 or >= 1150000 and < 1160000).OrderBy(_ => rng.Next()).Take(600).ToArray();
        var weights = new double[pool.Length];
        double total = 0;
        for (int i = 0; i < pool.Length; i++) total += weights[i] = 1.0 / Math.Pow(i + 1, 1.1);
        var ids = new int[20000];
        for (int k = 0; k < ids.Length; k++)
        {
            double x = rng.NextDouble() * total;
            int i = 0;
            while (i < pool.Length - 1 && (x -= weights[i]) > 0) i++;
            ids[k] = pool[i];
        }
        return ids;
    }
}
