using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using BufferPoolBench;

// Usage: BufferPoolBench <pair|burst|worker|retain-shared|retain-large|all> [scale]
// Prints one line per measurement: scenario, pool, size, ns/op, allocated bytes/op, gen0/1/2 deltas.
string mode = args.Length > 0 ? args[0] : "all";
double scale = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;

Console.WriteLine($"# runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
                  $"server_gc={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} cpus={Environment.ProcessorCount} " +
                  $"tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"} scale={scale}");

int[] sizes = [256, 4 << 10, 64 << 10, 256 << 10, 1 << 20, 4 << 20, 8 << 20];
var large = new LargeBufferPool<byte>(64L << 20);

if (mode is "pair" or "all")
{
    Pair(sizes, large, scale);
}

if (mode is "burst" or "all")
{
    Burst(sizes, large, scale);
}

if (mode is "worker" or "all")
{
    Worker(scale);
}

if (mode == "retain-shared")
{
    Retain(useLarge: false);
}

if (mode == "retain-large")
{
    Retain(useLarge: true);
}

return;

static (long alloc, int g0, int g1, int g2) Snap() =>
    (GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

static void Report(string scenario, string pool, long size, long ops, long ticks, (long alloc, int g0, int g1, int g2) a,
    (long alloc, int g0, int g1, int g2) b, string extra = "")
{
    double ns = ticks * 1e9 / Stopwatch.Frequency / ops;
    Console.WriteLine($"{scenario,-8} {pool,-14} {size,9} B  {ns,10:F1} ns/op  {(double)(b.alloc - a.alloc) / ops,12:F1} B/op  " +
                      $"gc {b.g0 - a.g0}/{b.g1 - a.g1}/{b.g2 - a.g2} {extra}");
}

static long Iterations(int size, double scale) => Math.Max(200, (long)(scale * (size <= 4096 ? 4_000_000 : size <= 262144 ? 1_000_000 : 50_000)));

// A: one rent then one return on one thread (the scoped scratch pattern: PackSprites, RecordStore, sync decodes).
static void Pair(int[] sizes, LargeBufferPool<byte> large, double scale)
{
    foreach (int size in sizes)
    {
        long n = Iterations(size, scale);
        Run("pair", "Shared", size, n, () => { var a = ArrayPool<byte>.Shared.Rent(size); a[0] = 1; ArrayPool<byte>.Shared.Return(a); });
        Run("pair", "ST", size, n, () => { var p = STArrayPool<byte>.ThreadLocal; var a = p.Rent(size); a[0] = 1; p.Return(a); });

        if (size >= LargeBufferPool<byte>.MinClassBytes)
        {
            Run("pair", "Large", size, n, () => { var a = large.Rent(size); a[0] = 1; large.Return(a); });
        }

        if (size <= 1 << 20)
        {
            long m = size <= 4096 ? n : Math.Max(200, n / 20);
            Run("pair", "new(uninit)", size, m, () => { var a = GC.AllocateUninitializedArray<byte>(size); a[0] = 1; GC.KeepAlive(a); });
        }
    }
}

static void Run(string scenario, string pool, int size, long n, Action op)
{
    for (int i = 0; i < Math.Min(n, 1000); i++)
    {
        op();
    }

    var a = Snap();
    long t0 = Stopwatch.GetTimestamp();

    for (long i = 0; i < n; i++)
    {
        op();
    }

    long t = Stopwatch.GetTimestamp() - t0;
    Report(scenario, pool, size, n, t, a, Snap());
}

// B: rent 8, then return 8 (a direction job's frames, a burst of region loads): the shared pool's per-thread slot holds one
// array per size, the rest go through its per-core locked stacks.
static void Burst(int[] sizes, LargeBufferPool<byte> large, double scale)
{
    var held = new byte[8][];

    foreach (int size in sizes)
    {
        long n = Math.Max(50, Iterations(size, scale) / 8);
        RunBurst("burst8", "Shared", size, n, held, () => ArrayPool<byte>.Shared.Rent(size), a => ArrayPool<byte>.Shared.Return(a));
        RunBurst("burst8", "ST", size, n, held, () => STArrayPool<byte>.ThreadLocal.Rent(size), a => STArrayPool<byte>.ThreadLocal.Return(a));

        if (size >= LargeBufferPool<byte>.MinClassBytes)
        {
            RunBurst("burst8", "Large", size, n, held, () => large.Rent(size), large.Return);
        }
    }
}

static void RunBurst(string scenario, string pool, int size, long n, byte[][] held, Func<byte[]> rent, Action<byte[]> ret)
{
    void Once()
    {
        for (int i = 0; i < held.Length; i++)
        {
            held[i] = rent();
            held[i][0] = 1;
        }

        for (int i = 0; i < held.Length; i++)
        {
            ret(held[i]);
            held[i] = null;
        }
    }

    for (int i = 0; i < 20; i++)
    {
        Once();
    }

    var a = Snap();
    long t0 = Stopwatch.GetTimestamp();

    for (long i = 0; i < n; i++)
    {
        Once();
    }

    long t = Stopwatch.GetTimestamp() - t0;
    Report(scenario, pool, size, n * held.Length, t, a, Snap(), "(per rent+return)");
}

// C: the decode pattern. W workers rent a buffer, fill its first bytes and hand it to the main thread, which returns it
// (FirstSightDecoder / DirectionJob / region loads). Sizes follow a mix of sprite and frame sizes with a few gump-sized ones.
static void Worker(double scale)
{
    int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    int perWorker = (int)Math.Max(2000, 200_000 * scale);
    int[] mix = [4 << 10, 16 << 10, 40 << 10, 100 << 10, 200 << 10, 400 << 10, 1 << 20, 4 << 20];
    int[] weight = [30, 25, 15, 12, 8, 6, 3, 1];
    var seq = new int[4096];
    var rng = new Random(1234);
    int total = weight.Sum();

    for (int i = 0; i < seq.Length; i++)
    {
        int r = rng.Next(total), k = 0;

        while (r >= weight[k])
        {
            r -= weight[k++];
        }

        seq[i] = mix[k];
    }

    var large = new LargeBufferPool<byte>(64L << 20);

    WorkerRun("Shared", workers, perWorker, seq, _ => ArrayPool<byte>.Shared.Rent, (a, _) => ArrayPool<byte>.Shared.Return(a));
    WorkerRun("ST-tls", workers, perWorker, seq, _ => n => STArrayPool<byte>.ThreadLocal.Rent(n), (a, _) => STArrayPool<byte>.ThreadLocal.Return(a));

    // ST pools owned by each worker; the main thread sends the buffer back to its owner through a queue that the owner drains
    // before each rent
    var owners = new OwnedPool[workers];

    for (int i = 0; i < workers; i++)
    {
        owners[i] = new OwnedPool();
    }

    WorkerRun("ST-owner-q", workers, perWorker, seq, w => owners[w].Rent, (a, w) => owners[w].Back.Enqueue(a));
    WorkerRun("Large", workers, perWorker, seq, _ => large.Rent, (a, _) => large.Return(a));
    WorkerRun("Hybrid", workers, perWorker, seq,
        _ => n => n > 64 << 10 ? large.Rent(n) : ArrayPool<byte>.Shared.Rent(n),
        (a, _) =>
        {
            if (a.Length > 64 << 10)
            {
                large.Return(a);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(a);
            }
        });
    Console.WriteLine($"# large pool after worker runs: rents {large.Rents} hits {large.Hits} misses {large.Misses} " +
                      $"alloc {large.AllocatedBytes >> 20} MiB dropped {large.Dropped} retained {large.RetainedBytes >> 20} MiB " +
                      $"high-water out {large.HighWaterBytes >> 20} MiB");
}

static void WorkerRun(string pool, int workers, int perWorker, int[] seq, Func<int, Func<int, byte[]>> rentFor, Action<byte[], int> ret)
{
    var queue = new BlockingCollection<(byte[] Buffer, int Owner)>(new ConcurrentQueue<(byte[], int)>(), 64);

    for (int pass = 0; pass < 2; pass++) // pass 0 warms up
    {
        var a = Snap();
        long t0 = Stopwatch.GetTimestamp();
        var threads = new Thread[workers];

        for (int w = 0; w < workers; w++)
        {
            int id = w;
            Func<int, byte[]> rent = rentFor(id);
            threads[w] = new Thread(() =>
            {
                int count = pass == 0 ? perWorker / 10 : perWorker;

                for (int i = 0; i < count; i++)
                {
                    var b = rent(seq[(i * 7 + id * 131) & (seq.Length - 1)]);
                    b[0] = (byte)i;
                    queue.Add((b, id));
                }
            }) { IsBackground = true };
            threads[w].Start();
        }

        int expected = workers * (pass == 0 ? perWorker / 10 : perWorker);

        for (int i = 0; i < expected; i++)
        {
            var (b, owner) = queue.Take();
            ret(b, owner);
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        long ticks = Stopwatch.GetTimestamp() - t0;

        if (pass == 1)
        {
            Report("worker", pool, 0, expected, ticks, a, Snap(), $"(workers {workers}, ns per buffer end to end incl. queue)");
        }
    }
}

// D: retention after a burst. 8 threads each hold 4 x 4 MiB (+ 4 x 1 MiB) at once, return them on the main thread; then a gen1
// collection (no gen2, so the shared pool's gen2 trim does not run) and the LOH size is read.
static void Retain(bool useLarge)
{
    var large = new LargeBufferPool<byte>(32L << 20);
    var bag = new ConcurrentBag<byte[]>();
    int[] sz = [4 << 20, 4 << 20, 4 << 20, 4 << 20, 1 << 20, 1 << 20, 1 << 20, 1 << 20];

    for (int round = 0; round < 3; round++)
    {
        var threads = new Thread[8];

        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                foreach (int s in sz)
                {
                    var b = useLarge ? large.Rent(s) : ArrayPool<byte>.Shared.Rent(s);
                    b[0] = 1;
                    bag.Add(b);
                }
            });
            threads[t].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        while (bag.TryTake(out var b))
        {
            if (useLarge)
            {
                large.Return(b);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(b);
            }
        }
    }

    GC.Collect(1, GCCollectionMode.Forced, blocking: true);
    long lohGen1 = GC.GetGCMemoryInfo(GCKind.Any).GenerationInfo[3].SizeAfterBytes;
    // two full collections: the first runs the shared pool's gen2 trim callback, the second frees what it dropped
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var g3 = GC.GetGCMemoryInfo(GCKind.Any).GenerationInfo[3];
    long lohLive = g3.SizeAfterBytes - g3.FragmentationAfterBytes;
    Console.WriteLine($"retain   {(useLarge ? "Large(32MiB)" : "Shared"),-14} 8 threads x 20 MiB held, 3 rounds  total alloc {GC.GetTotalAllocatedBytes(true) >> 20} MiB  " +
                      $"LOH after gen1 (live+garbage) {lohGen1 >> 20} MiB  LOH live after 2 full GCs (= pool retention) {lohLive >> 20} MiB" +
                      (useLarge ? $"  retained {large.RetainedBytes >> 20} MiB misses {large.Misses}" : ""));
}

internal sealed class OwnedPool
{
    public readonly ConcurrentQueue<byte[]> Back = new ConcurrentQueue<byte[]>();
    private readonly STArrayPool<byte> _pool = new STArrayPool<byte>();

    public byte[] Rent(int n)
    {
        while (Back.TryDequeue(out var b))
        {
            _pool.Return(b);
        }

        return _pool.Rent(n);
    }
}
