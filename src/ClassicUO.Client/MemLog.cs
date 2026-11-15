#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ClassicUO.Utility.Logging;

namespace ClassicUO
{
    // CUO_MEM_LOG=<seconds>: a background thread logs the GC heap every <seconds> (docs/modern-client/memory-footprint.md).
    // Pairs with an external sampler that reads the process's private working set and commit; this side adds what only the
    // runtime knows (GC heap size, committed, fragmentation). Dev builds only; unset, nothing starts and nothing is checked
    // per frame.
    internal static class MemLog
    {
        // Unset, this allocates nothing: the closures live in the Start* helpers, which run only after their checks (a
        // lambda here would allocate its captured locals' display class on entry, before the checks).
        public static void Start()
        {
            string regions = Environment.GetEnvironmentVariable("CUO_MEM_REGIONS");

            if (!string.IsNullOrEmpty(regions) && OperatingSystem.IsWindows())
            {
                StartRegions(regions);
            }

            string value = Environment.GetEnvironmentVariable("CUO_MEM_LOG");

            if (string.IsNullOrEmpty(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
            {
                return;
            }

            StartLog(seconds);
        }

        private static void StartLog(double seconds)
        {
            var thread = new Thread(() => Run(seconds)) { IsBackground = true, Name = "CUO_MEM_LOG", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        // CUO_MEM_REGIONS=<s>,<s>,...: at those seconds after start, logs every private allocation (one VirtualAlloc
        // reservation) with 1 MiB or more committed: base, reserved, committed and the protections (0x4 read-write,
        // 0x404 write-combined). The external walk keeps only the largest 40; this names the rest (mem-reduce-2).
        private static void StartRegions(string value)
        {
            var times = new System.Collections.Generic.List<double>();

            foreach (string s in value.Split(','))
            {
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t >= 0)
                {
                    times.Add(t);
                }
            }

            times.Sort();
            var thread = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();

                foreach (double t in times)
                {
                    int wait = (int) (t * 1000 - sw.ElapsedMilliseconds);

                    if (wait > 0)
                    {
                        Thread.Sleep(wait);
                    }

                    LogRegions(sw.Elapsed.TotalSeconds);
                }
            }) { IsBackground = true, Name = "CUO_MEM_REGIONS", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public nint BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public nint RegionSize;
            public uint State, Protect, Type;
        }

        [DllImport("kernel32.dll")]
        private static extern nint VirtualQuery(nint address, out MemoryBasicInformation info, nint length);

        private static void LogRegions(double t)
        {
            const uint MemCommit = 0x1000, MemPrivate = 0x20000;
            const double MB = 1024.0 * 1024.0;
            var inv = CultureInfo.InvariantCulture;
            var allocs = new System.Collections.Generic.Dictionary<nint, (long reserved, long commit, System.Collections.Generic.HashSet<uint> prots)>();
            nint addr = 0;

            while (VirtualQuery(addr, out MemoryBasicInformation mbi, Marshal.SizeOf<MemoryBasicInformation>()) != 0)
            {
                if (mbi.Type == MemPrivate && mbi.State != 0x10000)
                {
                    allocs.TryGetValue(mbi.AllocationBase, out var a);
                    a.reserved += mbi.RegionSize;

                    if (mbi.State == MemCommit)
                    {
                        a.commit += mbi.RegionSize;
                        // numbers, not substrings: "0x404" contains "0x4" and "0x40"
                        (a.prots ??= new System.Collections.Generic.HashSet<uint>()).Add(mbi.Protect);
                    }

                    allocs[mbi.AllocationBase] = a;
                }

                nint next = mbi.BaseAddress + mbi.RegionSize;

                if (next <= addr)
                {
                    break;
                }

                addr = next;
            }

            long total = 0;
            int n = 0;
            var sb = new System.Text.StringBuilder();

            foreach (var kv in allocs)
            {
                total += kv.Value.commit;

                if (kv.Value.commit >= 1 << 20)
                {
                    n++;
                    sb.Append(string.Format(inv, "\n[memregions]   0x{0:x} reserved {1:0.0} commit {2:0.0} MiB {3}", (long) kv.Key, kv.Value.reserved / MB, kv.Value.commit / MB, FormatProtections(kv.Value.prots)));
                }
            }

            Log.Info(string.Format(inv, "[memregions] t {0:0.0} s: {1} private allocations, {2:0.0} MiB committed; {3} with 1 MiB or more:{4}", t, allocs.Count, total / MB, n, sb));
        }

        // "0x4|0x404", ascending
        private static string FormatProtections(System.Collections.Generic.HashSet<uint> prots)
        {
            if (prots == null)
            {
                return "";
            }

            uint[] sorted = new uint[prots.Count];
            prots.CopyTo(sorted);
            Array.Sort(sorted);
            var sb = new System.Text.StringBuilder();

            foreach (uint p in sorted)
            {
                sb.Append(sb.Length == 0 ? "0x" : "|0x").Append(p.ToString("x", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static void Run(double seconds)
        {
            var inv = CultureInfo.InvariantCulture;
            const double MB = 1024.0 * 1024.0;
            GCMemoryInfo first = GC.GetGCMemoryInfo();

            Log.Info(string.Format(inv, "[mem] start: {0}, server GC {1}, concurrent {2}, latency {3}, total available {4:0} MB, every {5} s",
                RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT", GCSettings.IsServerGC,
                AppContext.TryGetSwitch("System.GC.Concurrent", out bool c) ? c.ToString() : "default", GCSettings.LatencyMode,
                first.TotalAvailableMemoryBytes / MB, seconds));

            var sw = Stopwatch.StartNew();
            int sleep = (int) Math.Max(100, seconds * 1000);

            while (true)
            {
                Thread.Sleep(sleep);
                GCMemoryInfo gi = GC.GetGCMemoryInfo(GCKind.Any);
                ReadOnlySpan<GCGenerationInfo> gens = gi.GenerationInfo;
                string g = "";

                for (int i = 0; i < gens.Length; i++)
                {
                    g += string.Format(inv, " g{0} {1:0.0}", i, gens[i].SizeAfterBytes / MB);
                }

                Log.Info(string.Format(inv, "[mem] t {0:0.0} s: gc heap {1:0.0} MB, committed {2:0.0} MB, fragmented {3:0.0} MB, live now {4:0.0} MB, ws {5:0.0} MB, gcs {6}/{7}/{8}, allocated {9:0.0} MB; after last gc (MB){10}",
                    sw.Elapsed.TotalSeconds, gi.HeapSizeBytes / MB, gi.TotalCommittedBytes / MB, gi.FragmentedBytes / MB,
                    GC.GetTotalMemory(false) / MB, Environment.WorkingSet / MB, GC.CollectionCount(0), GC.CollectionCount(1),
                    GC.CollectionCount(2), GC.GetTotalAllocatedBytes() / MB, g));
            }
        }
    }
}
#endif
