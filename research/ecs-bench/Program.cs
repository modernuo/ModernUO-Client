using EcsBench;

// Custom harness instead of BenchmarkDotNet so the exact same binary runs under JIT and NativeAOT.
// Usage: EcsBench [benchFilter] [libFilter]

var benches = new (string Name, int Warmup, int Measure, bool Parallel, Func<Adapter, long> Op)[]
{
    ("RenderExtract",         30,  150, false, a => a.RenderExtract()),
    ("RenderExtractParallel", 30,  150, true,  a => a.RenderExtractParallel()),
    ("MobileTick",           200, 3000, false, a => a.MobileTick()),
    ("PacketLookups",         50,  500, false, a => a.PacketLookups()),
    ("ChurnFrame",           100, 1500, false, a => a.ChurnFrame()),
    ("ChurnManyQueries",     100, 1500, false, a => a.ChurnManyQueries()),
    ("BlockStream",           20,  400, false, a => a.BlockStream()),
    ("DirtyTag",             100, 1500, false, a => a.DirtyTag()),
    ("DirtyTicks",           100, 1500, false, a => a.DirtyTicks()),
    ("Snapshot",             200, 3000, false, a => a.Snapshot()),
};
var libs = new (string Name, Func<Adapter> Make)[]
{
    ("TinyEcs", () => new TinyEcsAdapter()),
    ("Friflo",  () => new FrifloAdapter()),
    ("Arch",    () => new ArchAdapter()),
    ("Flecs",   () => new FlecsAdapter()),
};

var benchFilter = args.Length > 0 ? args[0] : "*";
var libFilter = args.Length > 1 ? args[1] : "*";
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
var mode = RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "AOT";
Console.WriteLine($"mode={mode} cores={Environment.ProcessorCount} runtime={Environment.Version}");

// Populate cost + retained heap, once per library.
var rows = new List<string>();
Console.WriteLine();
Console.WriteLine("| Library | Populate (555k entities) ms | Retained heap MB |");
Console.WriteLine("|---|---:|---:|");
foreach (var (lib, make) in libs)
{
    if (libFilter != "*" && lib != libFilter) continue;
    Collect();
    var before = GC.GetTotalMemory(true);
    var sw = Stopwatch.StartNew();
    var a = make(); a.Populate();
    sw.Stop();
    Collect();
    var after = GC.GetTotalMemory(true);
    Console.WriteLine($"| {lib} | {sw.Elapsed.TotalMilliseconds:F0} | {(after - before) / 1048576.0:F1} |");
    GC.KeepAlive(a); a.Dispose();
}

Console.WriteLine();
Console.WriteLine("| Benchmark | Library | median us | p95 us | alloc B/op | gen0 | checksum |");
Console.WriteLine("|---|---|---:|---:|---:|---:|---|");
foreach (var (bench, warm, measure, parallel, op) in benches)
{
    if (benchFilter != "*" && bench != benchFilter) continue;
    foreach (var (lib, make) in libs)
    {
        if (libFilter != "*" && lib != libFilter) continue;
        var a = make();
        a.Populate();
        Collect();
        long checksum = 0;
        try
        {
            for (int i = 0; i < warm; i++) checksum += op(a);
        }
        catch (NotSupportedException)
        {
            Console.WriteLine($"| {bench} | {lib} | n/a | | | | |");
            a.Dispose();
            continue;
        }

        var times = new double[measure];
        var gen0 = GC.CollectionCount(0);
        long alloc0 = parallel ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measure; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            checksum += op(a);
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        long alloc1 = parallel ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
        var gens = GC.CollectionCount(0) - gen0;
        Array.Sort(times);
        var med = times[measure / 2];
        var p95 = times[(int)(measure * 0.95)];
        Console.WriteLine($"| {bench} | {lib} | {med:F1} | {p95:F1} | {(alloc1 - alloc0) / (double)measure:F0} | {gens} | {checksum} |");
        a.Dispose();
    }
}

static void Collect()
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
}

// Arch's JobScheduler / Friflo's ParallelJobRunner leave foreground worker threads alive; force exit.
Environment.Exit(0);
