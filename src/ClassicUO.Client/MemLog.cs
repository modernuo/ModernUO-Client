#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
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
        public static void Start()
        {
            string value = Environment.GetEnvironmentVariable("CUO_MEM_LOG");

            if (string.IsNullOrEmpty(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
            {
                return;
            }

            var thread = new Thread(() => Run(seconds)) { IsBackground = true, Name = "CUO_MEM_LOG", Priority = ThreadPriority.BelowNormal };
            thread.Start();
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
