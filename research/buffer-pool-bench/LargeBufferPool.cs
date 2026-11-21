using System.Numerics;
using System.Runtime.CompilerServices;

namespace BufferPoolBench;

/// <summary>
/// Thread-safe pool for large (LOH-sized) arrays: any thread rents, any thread returns. Size classes are four per
/// octave (2^k x 1, 1.25, 1.5, 1.75), so a rent wastes at most 25 % instead of the shared pool's 100 %. The free
/// buffers it keeps are bounded by a byte budget: a return that would exceed it evicts the least recently returned free
/// buffers (or drops the returned one). <see cref="Trim"/> drops free buffers idle longer than an age.
/// Requests above the largest class are allocated exactly and never pooled.
/// </summary>
public sealed class LargeBufferPool<T>
{
    public const int MinClassBytes = 64 << 10;
    public const int MaxClassBytes = 64 << 20;
    private const int MaxPerClass = 32;

    private static readonly int ElemSize = Unsafe.SizeOf<T>();
    private static readonly int MinLog2 = BitOperations.Log2((uint)MinClassBytes);
    private static readonly int ClassCount = (BitOperations.Log2((uint)MaxClassBytes) - MinLog2) * 4 + 1;

    private readonly object _lock = new object();
    private readonly T[][][] _free;
    private readonly long[][] _stamp;
    private readonly int[] _count;
    private readonly long _budgetBytes;
    private long _retainedBytes, _clock;

    public long Rents, Hits, Misses, AllocatedBytes, Dropped, OutstandingBytes, HighWaterBytes;

    public LargeBufferPool(long budgetBytes)
    {
        _budgetBytes = budgetBytes;
        _free = new T[ClassCount][][];
        _stamp = new long[ClassCount][];
        _count = new int[ClassCount];

        for (int i = 0; i < ClassCount; i++)
        {
            _free[i] = new T[MaxPerClass][];
            _stamp[i] = new long[MaxPerClass];
        }
    }

    public long RetainedBytes => Volatile.Read(ref _retainedBytes);

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

    public T[] Rent(int minimumLength)
    {
        long bytes = (long)minimumLength * ElemSize;
        int cls = ClassOf(bytes);
        if (cls >= 0)
        {
            lock (_lock)
            {
                Rents++;
                int n = _count[cls];

                if (n > 0)
                {
                    n--;
                    T[] a = _free[cls][n];
                    _free[cls][n] = null;
                    _count[cls] = n;
                    _retainedBytes -= (long)a.Length * ElemSize;
                    Hits++;
                    Out((long)a.Length * ElemSize);

                    return a;
                }
            }
        }

        int length = cls >= 0 ? (int)(ClassBytes(cls) / ElemSize) : minimumLength;
        T[] fresh = GC.AllocateUninitializedArray<T>(length);

        lock (_lock)
        {
            if (cls < 0)
            {
                Rents++;
            }

            Misses++;
            AllocatedBytes += (long)length * ElemSize;
            Out((long)length * ElemSize);
        }

        return fresh;
    }

    private void Out(long bytes)
    {
        OutstandingBytes += bytes;

        if (OutstandingBytes > HighWaterBytes)
        {
            HighWaterBytes = OutstandingBytes;
        }
    }

    public void Return(T[] array)
    {
        if (array == null)
        {
            return;
        }

        long bytes = (long)array.Length * ElemSize;
        int cls = ClassOf(bytes);

        lock (_lock)
        {
            OutstandingBytes -= bytes;

            if (cls < 0 || ClassBytes(cls) != bytes || bytes > _budgetBytes)
            {
                Dropped++;

                return;
            }

            while (_retainedBytes + bytes > _budgetBytes && EvictOldestLocked())
            {
            }

            int n = _count[cls];

            if (n == MaxPerClass)
            {
                // drop the oldest of this class (the bottom of its stack)
                _retainedBytes -= bytes;
                Array.Copy(_free[cls], 1, _free[cls], 0, n - 1);
                Array.Copy(_stamp[cls], 1, _stamp[cls], 0, n - 1);
                n--;
                Dropped++;
            }

            _free[cls][n] = array;
            _stamp[cls][n] = ++_clock;
            _count[cls] = n + 1;
            _retainedBytes += bytes;
        }
    }

    private bool EvictOldestLocked()
    {
        int best = -1;
        long bestStamp = long.MaxValue;

        for (int c = 0; c < _count.Length; c++)
        {
            if (_count[c] > 0 && _stamp[c][0] < bestStamp)
            {
                bestStamp = _stamp[c][0];
                best = c;
            }
        }

        if (best < 0)
        {
            return false;
        }

        int n = _count[best];
        _retainedBytes -= ClassBytes(best);
        Array.Copy(_free[best], 1, _free[best], 0, n - 1);
        Array.Copy(_stamp[best], 1, _stamp[best], 0, n - 1);
        _free[best][n - 1] = null;
        _count[best] = n - 1;
        Dropped++;

        return true;
    }

    /// <summary>Drops every free buffer (the explicit trim: facet change, memory pressure, shutdown).</summary>
    public void Trim()
    {
        lock (_lock)
        {
            for (int c = 0; c < _count.Length; c++)
            {
                Array.Clear(_free[c]);
                _count[c] = 0;
            }

            _retainedBytes = 0;
        }
    }
}
