// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted from ModernUO Projects/Server/Buffers/STArrayPool.cs (commit 3161a5ba4), itself an adaptation of .NET's
// TlsOverPerCoreLockedStacksArrayPool. Changes: one instance per owning thread ([ThreadStatic] ThreadLocal) instead of
// one Shared instance for one game thread; the cache slots are per instance; the gen2 trim is a flag (Gen2Epoch) the
// owner thread reads on its next Rent instead of a post to an event loop; Environment.TickCount64 for trim ages;
// counters and the CUO_DEVTOOLS rent/return audit (PoolAudit) instead of DEBUG_ARRAYPOOL.

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Uo.Buffers;

/// <summary>
/// Single-threaded array pool: one instance per thread (<see cref="ThreadLocal"/>). Not thread-safe: an array rented
/// here must be returned on the same thread. Arrays that cross threads come from <see cref="BufferPool.RentShared{T}"/>.
/// The facade routes LOH-sized requests to <see cref="LargeBufferPool"/>, so an ST pool reached through it holds only
/// small arrays.
/// </summary>
public sealed class STArrayPool<T> : ArrayPool<T>, IStPoolStats
{
    private const int StackArraySize = 32;
    private const int BucketCount = 27; // SelectBucketIndex(1024 * 1024 * 1024 + 1)

    [ThreadStatic] private static STArrayPool<T> t_current;

    /// <summary>The calling thread's pool, created on first use. It dies with the thread.</summary>
    public static STArrayPool<T> ThreadLocal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current ?? CreateForThread();
    }

    private readonly STArray[] _cache = new STArray[BucketCount];
    private STArrayStack[] _buckets = new STArrayStack[BucketCount];
    private int _gen2Seen = Gen2Epoch.Value;

    internal readonly int OwnerThreadId = Environment.CurrentManagedThreadId;

    /// <summary>Counters, plain fields: only the owner thread writes them.</summary>
    public long Rents, Misses, AllocatedBytes, Trims;

    long IStPoolStats.Rents => Rents;
    long IStPoolStats.Misses => Misses;
    long IStPoolStats.AllocatedBytes => AllocatedBytes;

    private STArrayPool()
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static STArrayPool<T> CreateForThread()
    {
        var pool = new STArrayPool<T>();
        t_current = pool;
        PoolStats.Register(pool);

        return pool;
    }

    public override T[] Rent(int minimumLength)
    {
#if CUO_DEVTOOLS
        if (PoolAudit.Enabled)
        {
            PoolAudit.CheckThread(OwnerThreadId, "ST rent");
        }
#endif
        if (_gen2Seen != Gen2Epoch.Value)
        {
            _gen2Seen = Gen2Epoch.Value;
            Trim();
        }

        Rents++;
        T[] buffer;

        int bucketIndex = SelectBucketIndex(minimumLength);

        if ((uint)bucketIndex < (uint)_cache.Length)
        {
            buffer = _cache[bucketIndex].Array;

            if (buffer is not null)
            {
                _cache[bucketIndex].Array = null;
#if CUO_DEVTOOLS
                PoolAudit.OnRent(buffer);
#endif
                return buffer;
            }

            STArrayStack b = _buckets[bucketIndex];

            if (b is not null)
            {
                buffer = b.TryPop();

                if (buffer is not null)
                {
#if CUO_DEVTOOLS
                    PoolAudit.OnRent(buffer);
#endif
                    return buffer;
                }
            }

            minimumLength = GetMaxSizeForBucket(bucketIndex);
        }

        if (minimumLength == 0)
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

        Misses++;
        AllocatedBytes += (long)minimumLength * Unsafe.SizeOf<T>();
        T[] array = GC.AllocateUninitializedArray<T>(minimumLength);
#if CUO_DEVTOOLS
        PoolAudit.OnRent(array);
#endif

        return array;
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        if (array is null)
        {
            return;
        }

#if CUO_DEVTOOLS
        if (PoolAudit.Enabled)
        {
            PoolAudit.CheckThread(OwnerThreadId, "ST return");
        }
#endif
        int bucketIndex = SelectBucketIndex(array.Length);

        if ((uint)bucketIndex < (uint)_cache.Length)
        {
            if (array.Length != GetMaxSizeForBucket(bucketIndex))
            {
                throw new ArgumentException("Buffer is not from the pool", nameof(array));
            }

#if CUO_DEVTOOLS
            PoolAudit.OnReturn(array);
#endif
            if (clearArray)
            {
                Array.Clear(array);
            }

            ref STArray slot = ref _cache[bucketIndex];
            T[] prev = slot.Array;
            slot = new STArray(array);

            if (prev is not null)
            {
                (_buckets[bucketIndex] ??= new STArrayStack()).TryPush(prev);
            }
        }
    }

    /// <summary>Bytes this pool holds free. Walks 27 buckets; for stats only (a racy read from another thread).</summary>
    public long RetainedBytes
    {
        get
        {
            long bytes = 0;

            for (int i = 0; i < BucketCount; i++)
            {
                if (_cache[i].Array is not null)
                {
                    bytes += (long)GetMaxSizeForBucket(i) * Unsafe.SizeOf<T>();
                }

                if (_buckets[i] is { } s)
                {
                    bytes += (long)s.Count * GetMaxSizeForBucket(i) * Unsafe.SizeOf<T>();
                }
            }

            return bytes;
        }
    }

    internal void ResetForTesting()
    {
        Array.Clear(_cache);
        _buckets = new STArrayStack[BucketCount];
    }

    /// <summary>Drops cached arrays by age and memory pressure (ModernUO's thresholds). Owner thread only.</summary>
    public void Trim()
    {
        Trims++;
        long ticks = Environment.TickCount64;
        MemoryPressure pressure = GetMemoryPressure();

        STArrayStack[] buckets = _buckets;

        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i]?.Trim(ticks, pressure, GetMaxSizeForBucket(i));
        }

        if (pressure == MemoryPressure.High)
        {
            Array.Clear(_cache);

            return;
        }

        uint threshold = pressure switch
        {
            MemoryPressure.Medium => 10000,
            _ => 30000,
        };

        for (int i = 0; i < _cache.Length; i++)
        {
            ref STArray b = ref _cache[i];

            if (b.Array is null)
            {
                continue;
            }

            long lastSeen = b.Ticks;

            if (lastSeen == 0)
            {
                b.Ticks = ticks;
            }
            else if (ticks - lastSeen >= threshold)
            {
                b.Array = null;
            }
        }
    }

    // Buffers are bucketed so that a request between 2^(n-1) + 1 and 2^n is given a buffer of 2^n
    // Bucket index is log2(bufferSize - 1) with the exception that buffers between 1 and 16 bytes
    // are combined, and the index is slid down by 3 to compensate.
    // Zero is a valid bufferSize, and it is assigned the highest bucket index so that zero-length
    // buffers are not retained by the pool. The pool will return the Array.Empty singleton for these.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SelectBucketIndex(int bufferSize) => BitOperations.Log2((uint)bufferSize - 1 | 15) - 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetMaxSizeForBucket(int binIndex)
    {
        int maxSize = 16 << binIndex;
        Debug.Assert(maxSize >= 0);

        return maxSize;
    }

    internal enum MemoryPressure
    {
        Low,
        Medium,
        High
    }

    internal static MemoryPressure GetMemoryPressure()
    {
        GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();

        if (memoryInfo.MemoryLoadBytes >= memoryInfo.HighMemoryLoadThresholdBytes * 0.90)
        {
            return MemoryPressure.High;
        }

        if (memoryInfo.MemoryLoadBytes >= memoryInfo.HighMemoryLoadThresholdBytes * 0.70)
        {
            return MemoryPressure.Medium;
        }

        return MemoryPressure.Low;
    }

    private sealed class STArrayStack
    {
        // Maximum buffers we will store in our stack
        private readonly T[][] _arrays = new T[StackArraySize][];
        private int _count;
        private long _ticks;

        public int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPush(T[] array)
        {
            T[][] arrays = _arrays;
            int count = _count;

            if ((uint)count < (uint)arrays.Length)
            {
                arrays[count] = array;
                _count = count + 1;

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] TryPop()
        {
            T[][] arrays = _arrays;
            int count = _count - 1;

            if ((uint)count < (uint)arrays.Length)
            {
                T[] arr = arrays[count];
                arrays[count] = null;
                _count = count;

                return arr;
            }

            return null;
        }

        public void Trim(long now, MemoryPressure pressure, int bucketSize)
        {
            if (_count == 0)
            {
                return;
            }

            // 10 seconds under high pressure, otherwise 60 seconds
            int threshold = pressure == MemoryPressure.High ? 10000 : 60000;

            if (_ticks == 0)
            {
                _ticks = now;

                return;
            }

            if (now - _ticks <= threshold)
            {
                return;
            }

            int trimCount = 1;

            switch (pressure)
            {
                case MemoryPressure.Medium:
                    trimCount = 2;

                    break;
                case MemoryPressure.High:
                    if (bucketSize > 16384)
                    {
                        trimCount++;
                    }

                    int size = Unsafe.SizeOf<T>();

                    if (size > 32)
                    {
                        trimCount += 2;
                    }
                    else if (size > 16)
                    {
                        trimCount++;
                    }

                    break;
            }

            while (_count > 0 && trimCount-- > 0)
            {
                _arrays[--_count] = null;
            }
        }
    }

    private struct STArray
    {
        public T[] Array;
        public long Ticks;

        public STArray(T[] array)
        {
            Array = array;
            Ticks = 0;
        }
    }
}
