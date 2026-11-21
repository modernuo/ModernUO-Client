// SPDX-License-Identifier: BSD-2-Clause

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Uo.Buffers;

/// <summary>What a large buffer is for. Each use keeps its own free buffers under its own cap, so one use cannot evict
/// another's (a burst of decode outputs pushing out the region buffers the next teleport needs).</summary>
public enum LargeUse : byte
{
    /// <summary>Scoped scratch (compressed inputs) and worker decode outputs: bounded by the threads and the decode budgets.</summary>
    General,

    /// <summary>Logical map region buffers of the two region caches (Uo.Render ChunkStore, client MapLoader): a teleport
    /// swaps them. A rent may take a free buffer up to <see cref="LargeBufferPool.RegionFitClasses"/> classes larger.</summary>
    Region,

    /// <summary>Pack page chunk buffers (PackPages LRU caches): one per chunk decode in flight per lane.</summary>
    Page,
}

/// <summary>
/// Thread-safe pool for large arrays (above <see cref="BufferPool.SmallMaxBytes"/>, mostly LOH): any thread rents, any
/// thread returns. Size classes are four per octave (2^k x 1, 1.25, 1.5, 1.75), so a rent wastes at most 25 % instead
/// of a power-of-two pool's 100 %. Free buffers are kept per <see cref="LargeUse"/>, each under its own byte and count
/// cap (<see cref="SetCaps"/>; the host derives them from its thread counts, decode budgets and region caches): a return
/// that would exceed its use's cap evicts that use's least recently returned free buffers. Outstanding (rented) bytes are
/// counted, not capped; that is the job of DecodeBudget and the caches. Requests above the largest class are allocated
/// exactly and dropped on return.
/// </summary>
public static class LargeBufferPool
{
    public const int MinClassBytes = 64 << 10;
    public const int MaxClassBytes = 64 << 20;

    /// <summary>Caps until the host sets derived ones (<see cref="SetCaps"/>).</summary>
    public const long DefaultGeneralCapBytes = 32L << 20, DefaultRegionCapBytes = 32L << 20, DefaultPageCapBytes = 6L << 20;

    public const int DefaultRegionCapCount = 8, DefaultPageCapCount = 6;

    /// <summary>A region rent may reuse a free region buffer this many classes (25 % each) above its own: region sizes
    /// spread over several classes, so an exact match alone would rarely reuse the buffers the last teleport freed.</summary>
    public const int RegionFitClasses = 2;

    /// <summary>Age after which a free General or Page buffer goes back to the GC. Region buffers are not idle-trimmed:
    /// their cap is one teleport's swap set, which the next teleport needs however long the player stood still.</summary>
    public const long DefaultIdleMs = 180_000;
    private const int MaxPerClass = 32;
    private const int MaxTypes = 4;
    public const int UseCount = 3;

    private static readonly int MinLog2 = BitOperations.Log2((uint)MinClassBytes);
    internal static readonly int ClassCount = (BitOperations.Log2((uint)MaxClassBytes) - MinLog2) * 4 + 1;
    private static readonly int SlotsPerUse = MaxTypes * ClassCount;

    private static readonly object _lock = new object();
    private static readonly Array[][] _free = new Array[UseCount * MaxTypes * ClassCount][];
    private static readonly long[][] _stamp = new long[UseCount * MaxTypes * ClassCount][];
    private static readonly int[] _count = new int[UseCount * MaxTypes * ClassCount];
    private static readonly long[] _classHits = new long[ClassCount];
    private static readonly long[] _classMisses = new long[ClassCount];
    private static int _typeCount;
    private static long _retainedBytes;

    // indexed by LargeUse
    private static readonly long[] _capBytes = { DefaultGeneralCapBytes, DefaultRegionCapBytes, DefaultPageCapBytes };
    private static readonly int[] _capCount = { int.MaxValue, DefaultRegionCapCount, DefaultPageCapCount };
    private static readonly long[] _useFreeBytes = new long[UseCount];
    private static readonly int[] _useFreeCount = new int[UseCount];
    private static readonly long[] _useFreeHigh = new long[UseCount];
    private static readonly long[] _useRents = new long[UseCount];
    private static readonly long[] _useHits = new long[UseCount];
    private static readonly long[] _useMisses = new long[UseCount];
    private static readonly long[] _useAllocated = new long[UseCount];
    private static readonly long[] _useOutBytes = new long[UseCount];
    private static readonly int[] _useOutCount = new int[UseCount];
    private static readonly long[] _useOutHigh = new long[UseCount];
    private static readonly long[] _useDropped = new long[UseCount];
    private static readonly long[] _useEvicted = new long[UseCount];

    /// <summary>Counters (written under the lock; reads for stats may be torn by one update).</summary>
    public static long Rents, Hits, Misses, AllocatedBytes, Returns, Dropped, Evicted, OutstandingBytes, HighWaterBytes;

    public static long RetainedBytes => Volatile.Read(ref _retainedBytes);

    /// <summary>The sum of the uses' byte caps: the most free memory the pool can keep.</summary>
    public static long BudgetBytes
    {
        get
        {
            lock (_lock)
            {
                return _capBytes[0] + _capBytes[1] + _capBytes[2];
            }
        }
    }

    private static void SetDefaultCapsLocked()
    {
        _capBytes[(int)LargeUse.General] = DefaultGeneralCapBytes;
        _capCount[(int)LargeUse.General] = int.MaxValue;
        _capBytes[(int)LargeUse.Region] = DefaultRegionCapBytes;
        _capCount[(int)LargeUse.Region] = DefaultRegionCapCount;
        _capBytes[(int)LargeUse.Page] = DefaultPageCapBytes;
        _capCount[(int)LargeUse.Page] = DefaultPageCapCount;
    }

    /// <summary>Sets a use's free-buffer cap (bytes and buffer count); evicts down to it at once.</summary>
    public static void SetCaps(LargeUse use, long capBytes, int capCount)
    {
        int u = (int)use;

        lock (_lock)
        {
            _capBytes[u] = Math.Max(0, capBytes);
            _capCount[u] = Math.Max(0, capCount);

            while ((_useFreeBytes[u] > _capBytes[u] || _useFreeCount[u] > _capCount[u]) && EvictOldestLocked(u))
            {
            }
        }
    }

    public static long CapBytes(LargeUse use)
    {
        lock (_lock)
        {
            return _capBytes[(int)use];
        }
    }

    public static int CapCount(LargeUse use)
    {
        lock (_lock)
        {
            return _capCount[(int)use];
        }
    }

    public readonly record struct UseStats(long Rents, long Hits, long Misses, long AllocatedBytes, int OutCount, long OutBytes,
        long OutHighBytes, int FreeCount, long FreeBytes, long FreeHighBytes, long Dropped, long Evicted, long CapBytes, int CapCount);

    public static UseStats StatsOf(LargeUse use)
    {
        int u = (int)use;

        lock (_lock)
        {
            return new UseStats(_useRents[u], _useHits[u], _useMisses[u], _useAllocated[u], _useOutCount[u], _useOutBytes[u],
                _useOutHigh[u], _useFreeCount[u], _useFreeBytes[u], _useFreeHigh[u], _useDropped[u], _useEvicted[u], _capBytes[u],
                _capCount[u]);
        }
    }

    /// <summary>Class index of a byte size (rounded up), or -1 above the largest class.</summary>
    public static int ClassOf(long bytes)
    {
        if (bytes <= MinClassBytes)
        {
            return 0;
        }

        if (bytes > MaxClassBytes)
        {
            return -1;
        }

        int log = BitOperations.Log2((ulong)(bytes - 1)); // 2^log < bytes <= 2^(log+1)
        long baseBytes = 1L << log;
        int quarter = (int)(((bytes - baseBytes) * 4 + baseBytes - 1) / baseBytes); // 1..4

        return (log - MinLog2) * 4 + quarter;
    }

    public static long ClassBytes(int cls)
    {
        int log = MinLog2 + cls / 4;
        int q = cls % 4;

        return (1L << log) + ((1L << log) * q >> 2);
    }

    /// <summary>The array length a rent of <paramref name="minimumLength"/> elements returns.</summary>
    public static int PooledLength<T>(int minimumLength)
    {
        int cls = ClassOf((long)minimumLength * Unsafe.SizeOf<T>());

        return cls >= 0 ? (int)(ClassBytes(cls) / Unsafe.SizeOf<T>()) : minimumLength;
    }

    public static T[] Rent<T>(int minimumLength, LargeUse use = LargeUse.General)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

        int elem = Unsafe.SizeOf<T>();
        int cls = ClassOf((long)minimumLength * elem);
        int slot = TypeSlot<T>.Index;
        int u = (int)use;

        lock (_lock)
        {
            Rents++;
            _useRents[u]++;

            if (cls >= 0 && slot >= 0)
            {
                int baseIndex = u * SlotsPerUse + slot * ClassCount;
                int top = use == LargeUse.Region ? Math.Min(ClassCount - 1, cls + RegionFitClasses) : cls;

                for (int c = cls; c <= top; c++)
                {
                    int i = baseIndex + c;
                    int n = _count[i];

                    if (n == 0)
                    {
                        continue;
                    }

                    n--;
                    var a = (T[])_free[i][n];
                    _free[i][n] = null;
                    _count[i] = n;
                    long bytes = (long)a.Length * elem;
                    _retainedBytes -= bytes;
                    _useFreeBytes[u] -= bytes;
                    _useFreeCount[u]--;
                    Hits++;
                    _useHits[u]++;
                    _classHits[cls]++;
                    OutLocked(u, bytes);
#if CUO_DEVTOOLS
                    PoolAudit.OnRent(a);
#endif
                    return a;
                }
            }

            Misses++;
            _useMisses[u]++;

            if (cls >= 0)
            {
                _classMisses[cls]++;
            }
        }

        int length = cls >= 0 ? (int)(ClassBytes(cls) / elem) : minimumLength;
        T[] fresh = GC.AllocateUninitializedArray<T>(length);

        lock (_lock)
        {
            AllocatedBytes += (long)length * elem;
            _useAllocated[u] += (long)length * elem;
            OutLocked(u, (long)length * elem);
        }
#if CUO_DEVTOOLS
        PoolAudit.OnRent(fresh);
#endif

        return fresh;
    }

    /// <summary>Returns a rented array into the free buffers of <paramref name="use"/> (the use it was rented for).</summary>
    public static void Return<T>(T[] array, LargeUse use = LargeUse.General)
    {
        if (array is null)
        {
            return;
        }

#if CUO_DEVTOOLS
        PoolAudit.OnReturn(array);
#endif
        int elem = Unsafe.SizeOf<T>();
        long bytes = (long)array.Length * elem;
        int cls = ClassOf(bytes);
        int slot = TypeSlot<T>.Index;
        int u = (int)use;

        lock (_lock)
        {
            Returns++;
            OutstandingBytes -= bytes;
            _useOutBytes[u] -= bytes;
            _useOutCount[u]--;

            if (cls < 0 || slot < 0 || ClassBytes(cls) != bytes || bytes > _capBytes[u] || _capCount[u] == 0)
            {
                Dropped++;
                _useDropped[u]++;

                return;
            }

            while ((_useFreeBytes[u] + bytes > _capBytes[u] || _useFreeCount[u] >= _capCount[u]) && EvictOldestLocked(u))
            {
            }

            int i = u * SlotsPerUse + slot * ClassCount + cls;
            Array[] free = _free[i] ??= new Array[MaxPerClass];
            long[] stamp = _stamp[i] ??= new long[MaxPerClass];
            int n = _count[i];

            if (n == MaxPerClass)
            {
                RemoveBottomLocked(i);
                n--;
            }

            free[n] = array;
            stamp[n] = Environment.TickCount64;
            _count[i] = n + 1;
            _retainedBytes += bytes;
            _useFreeBytes[u] += bytes;
            _useFreeCount[u]++;

            if (_useFreeBytes[u] > _useFreeHigh[u])
            {
                _useFreeHigh[u] = _useFreeBytes[u];
            }
        }
    }

    private static void OutLocked(int u, long bytes)
    {
        OutstandingBytes += bytes;
        _useOutBytes[u] += bytes;
        _useOutCount[u]++;

        if (_useOutBytes[u] > _useOutHigh[u])
        {
            _useOutHigh[u] = _useOutBytes[u];
        }

        if (OutstandingBytes > HighWaterBytes)
        {
            HighWaterBytes = OutstandingBytes;
        }
    }

    // the least recently returned free buffer of one use
    private static bool EvictOldestLocked(int u)
    {
        int best = -1;
        long bestStamp = long.MaxValue;
        int end = (u + 1) * SlotsPerUse;

        for (int i = u * SlotsPerUse; i < end; i++)
        {
            if (_count[i] > 0 && _stamp[i][0] < bestStamp)
            {
                bestStamp = _stamp[i][0];
                best = i;
            }
        }

        if (best < 0)
        {
            return false;
        }

        RemoveBottomLocked(best);

        return true;
    }

    private static void RemoveBottomLocked(int i)
    {
        int n = _count[i];
        Array[] free = _free[i];
        // only exact class sizes are stored
        long bytes = ClassBytes(i % ClassCount);
        int u = i / SlotsPerUse;
        _retainedBytes -= bytes;
        _useFreeBytes[u] -= bytes;
        _useFreeCount[u]--;
        _useEvicted[u]++;
        Array.Copy(free, 1, free, 0, n - 1);
        Array.Copy(_stamp[i], 1, _stamp[i], 0, n - 1);
        free[n - 1] = null;
        _count[i] = n - 1;
        Evicted++;
    }

    /// <summary>Drops every free buffer (relog, memory pressure, shutdown).</summary>
    public static void Trim()
    {
        lock (_lock)
        {
            for (int i = 0; i < _count.Length; i++)
            {
                if (_count[i] > 0)
                {
                    Array.Clear(_free[i]);
                    _count[i] = 0;
                }
            }

            _retainedBytes = 0;
            Array.Clear(_useFreeBytes);
            Array.Clear(_useFreeCount);
        }
    }

    /// <summary>Drops General and Page free buffers returned more than <paramref name="maxIdleMs"/> ago. Region buffers
    /// stay (bounded by their cap, one teleport's swap set). A scan of the class stacks.</summary>
    public static void TrimIdle(long maxIdleMs)
    {
        long cutoff = Environment.TickCount64 - maxIdleMs;

        lock (_lock)
        {
            for (int u = 0; u < UseCount; u++)
            {
                if (u == (int)LargeUse.Region)
                {
                    continue;
                }

                int end = (u + 1) * SlotsPerUse;

                for (int i = u * SlotsPerUse; i < end; i++)
                {
                    while (_count[i] > 0 && _stamp[i][0] < cutoff)
                    {
                        RemoveBottomLocked(i);
                    }
                }
            }
        }
    }

    /// <summary>Per-class hits and misses for stats, as (classBytes, hits, misses) for classes with traffic.</summary>
    public static void CopyClassCounters(List<(long Bytes, long Hits, long Misses)> into)
    {
        lock (_lock)
        {
            for (int c = 0; c < ClassCount; c++)
            {
                if (_classHits[c] != 0 || _classMisses[c] != 0)
                {
                    into.Add((ClassBytes(c), _classHits[c], _classMisses[c]));
                }
            }
        }
    }

    internal static void ResetForTesting()
    {
        lock (_lock)
        {
            Trim();
            Rents = Hits = Misses = AllocatedBytes = Returns = Dropped = Evicted = OutstandingBytes = HighWaterBytes = 0;
            Array.Clear(_classHits);
            Array.Clear(_classMisses);
            Array.Clear(_useFreeHigh);
            Array.Clear(_useRents);
            Array.Clear(_useHits);
            Array.Clear(_useMisses);
            Array.Clear(_useAllocated);
            Array.Clear(_useOutBytes);
            Array.Clear(_useOutCount);
            Array.Clear(_useOutHigh);
            Array.Clear(_useDropped);
            Array.Clear(_useEvicted);
            SetDefaultCapsLocked();
        }
    }

    private static class TypeSlot<T>
    {
        // -1 when more than MaxTypes element types use the pool: those rent and drop without pooling.
        public static readonly int Index = Next();

        private static int Next()
        {
            int i = Interlocked.Increment(ref _typeCount) - 1;

            return i < MaxTypes ? i : -1;
        }
    }
}
