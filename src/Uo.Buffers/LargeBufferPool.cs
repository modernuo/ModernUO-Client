// SPDX-License-Identifier: BSD-2-Clause

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Uo.Buffers;

/// <summary>
/// Thread-safe pool for large arrays (above <see cref="BufferPool.SmallMaxBytes"/>, mostly LOH): any thread rents, any
/// thread returns. Size classes are four per octave (2^k x 1, 1.25, 1.5, 1.75), so a rent wastes at most 25 % instead
/// of a power-of-two pool's 100 %. One byte budget bounds the free buffers of every element type together: a return
/// that would exceed it evicts the least recently returned free buffers. Outstanding (rented) bytes are counted, not
/// capped; that is the job of DecodeBudget and the caches. Requests above the largest class are allocated exactly and
/// dropped on return.
/// </summary>
public static class LargeBufferPool
{
    public const int MinClassBytes = 64 << 10;
    public const int MaxClassBytes = 64 << 20;
    public const long DefaultBudgetBytes = 48L << 20;
    private const int MaxPerClass = 32;
    private const int MaxTypes = 4;

    private static readonly int MinLog2 = BitOperations.Log2((uint)MinClassBytes);
    internal static readonly int ClassCount = (BitOperations.Log2((uint)MaxClassBytes) - MinLog2) * 4 + 1;

    private static readonly object _lock = new object();
    private static readonly Array[][] _free = new Array[MaxTypes * ClassCount][];
    private static readonly long[][] _stamp = new long[MaxTypes * ClassCount][];
    private static readonly int[] _count = new int[MaxTypes * ClassCount];
    private static readonly long[] _classHits = new long[ClassCount];
    private static readonly long[] _classMisses = new long[ClassCount];
    private static int _typeCount;
    private static long _budgetBytes = DefaultBudgetBytes;
    private static long _retainedBytes;

    /// <summary>Counters (written under the lock; reads for stats may be torn by one update).</summary>
    public static long Rents, Hits, Misses, AllocatedBytes, Returns, Dropped, Evicted, OutstandingBytes, HighWaterBytes;

    public static long RetainedBytes => Volatile.Read(ref _retainedBytes);

    public static long BudgetBytes => Volatile.Read(ref _budgetBytes);

    /// <summary>Sets the free-buffer budget; evicts down to it at once.</summary>
    public static void SetBudget(long bytes)
    {
        lock (_lock)
        {
            _budgetBytes = Math.Max(0, bytes);

            while (_retainedBytes > _budgetBytes && EvictOldestLocked())
            {
            }
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

    public static T[] Rent<T>(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

        int elem = Unsafe.SizeOf<T>();
        int cls = ClassOf((long)minimumLength * elem);
        int slot = TypeSlot<T>.Index;

        lock (_lock)
        {
            Rents++;

            if (cls >= 0 && slot >= 0)
            {
                int i = slot * ClassCount + cls;
                int n = _count[i];

                if (n > 0)
                {
                    n--;
                    var a = (T[])_free[i][n];
                    _free[i][n] = null;
                    _count[i] = n;
                    long bytes = (long)a.Length * elem;
                    _retainedBytes -= bytes;
                    Hits++;
                    _classHits[cls]++;
                    OutLocked(bytes);
#if CUO_DEVTOOLS
                    PoolAudit.OnRent(a);
#endif
                    return a;
                }
            }

            Misses++;

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
            OutLocked((long)length * elem);
        }
#if CUO_DEVTOOLS
        PoolAudit.OnRent(fresh);
#endif

        return fresh;
    }

    public static void Return<T>(T[] array)
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

        lock (_lock)
        {
            Returns++;
            OutstandingBytes -= bytes;

            if (cls < 0 || slot < 0 || ClassBytes(cls) != bytes || bytes > _budgetBytes)
            {
                Dropped++;

                return;
            }

            while (_retainedBytes + bytes > _budgetBytes && EvictOldestLocked())
            {
            }

            int i = slot * ClassCount + cls;
            Array[] free = _free[i] ??= new Array[MaxPerClass];
            long[] stamp = _stamp[i] ??= new long[MaxPerClass];
            int n = _count[i];

            if (n == MaxPerClass)
            {
                _retainedBytes -= bytes;
                Array.Copy(free, 1, free, 0, n - 1);
                Array.Copy(stamp, 1, stamp, 0, n - 1);
                n--;
                Evicted++;
            }

            free[n] = array;
            stamp[n] = Environment.TickCount64;
            _count[i] = n + 1;
            _retainedBytes += bytes;
        }
    }

    private static void OutLocked(long bytes)
    {
        OutstandingBytes += bytes;

        if (OutstandingBytes > HighWaterBytes)
        {
            HighWaterBytes = OutstandingBytes;
        }
    }

    private static bool EvictOldestLocked()
    {
        int best = -1;
        long bestStamp = long.MaxValue;

        for (int i = 0; i < _count.Length; i++)
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
        _retainedBytes -= ClassBytes(i % ClassCount);
        Array.Copy(free, 1, free, 0, n - 1);
        Array.Copy(_stamp[i], 1, _stamp[i], 0, n - 1);
        free[n - 1] = null;
        _count[i] = n - 1;
        Evicted++;
    }

    /// <summary>Drops every free buffer (facet change, relog, memory pressure, shutdown).</summary>
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
        }
    }

    /// <summary>Drops free buffers returned more than <paramref name="maxIdleMs"/> ago. A scan of the class stacks.</summary>
    public static void TrimIdle(long maxIdleMs)
    {
        long cutoff = Environment.TickCount64 - maxIdleMs;

        lock (_lock)
        {
            for (int i = 0; i < _count.Length; i++)
            {
                while (_count[i] > 0 && _stamp[i][0] < cutoff)
                {
                    RemoveBottomLocked(i);
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
            _budgetBytes = DefaultBudgetBytes;
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
