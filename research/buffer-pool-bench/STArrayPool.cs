// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted from ModernUO Projects/Server/Buffers/STArrayPool.cs (commit 3161a5ba4), itself an adaptation of .NET's
// TlsOverPerCoreLockedStacksArrayPool. Changes for the benchmark prototype: one instance per owning thread (the cache
// slots are per instance, not static), no event-loop trim posting, Environment.TickCount64 for trim ages.

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BufferPoolBench;

/// <summary>
/// Single-threaded array pool: one instance per owning thread. Not thread-safe; every Rent and Return of one instance
/// must happen on the same thread.
/// </summary>
public sealed class STArrayPool<T> : ArrayPool<T>
{
    private const int StackArraySize = 32;
    private const int BucketCount = 27;

    [ThreadStatic] private static STArrayPool<T> t_current;

    /// <summary>The calling thread's pool (created on first use).</summary>
    public static STArrayPool<T> ThreadLocal => t_current ??= new STArrayPool<T>();

    private readonly T[][] _cache = new T[BucketCount][];
    private readonly STArrayStack[] _buckets = new STArrayStack[BucketCount];

    public override T[] Rent(int minimumLength)
    {
        int bucketIndex = SelectBucketIndex(minimumLength);

        if ((uint)bucketIndex < (uint)_cache.Length)
        {
            T[] buffer = _cache[bucketIndex];

            if (buffer != null)
            {
                _cache[bucketIndex] = null;

                return buffer;
            }

            STArrayStack b = _buckets[bucketIndex];

            if (b != null)
            {
                buffer = b.TryPop();

                if (buffer != null)
                {
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

        return GC.AllocateUninitializedArray<T>(minimumLength);
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        if (array == null)
        {
            return;
        }

        int bucketIndex = SelectBucketIndex(array.Length);

        if ((uint)bucketIndex < (uint)_cache.Length)
        {
            if (array.Length != GetMaxSizeForBucket(bucketIndex))
            {
                throw new ArgumentException("Buffer is not from the pool", nameof(array));
            }

            if (clearArray)
            {
                Array.Clear(array);
            }

            T[] prev = _cache[bucketIndex];
            _cache[bucketIndex] = array;

            if (prev != null)
            {
                (_buckets[bucketIndex] ??= new STArrayStack()).TryPush(prev);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SelectBucketIndex(int bufferSize) => BitOperations.Log2((uint)bufferSize - 1 | 15) - 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetMaxSizeForBucket(int binIndex) => 16 << binIndex;

    private sealed class STArrayStack
    {
        private readonly T[][] _arrays = new T[StackArraySize][];
        private int _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPush(T[] array)
        {
            int count = _count;

            if ((uint)count < (uint)_arrays.Length)
            {
                _arrays[count] = array;
                _count = count + 1;

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] TryPop()
        {
            int count = _count - 1;

            if ((uint)count < (uint)_arrays.Length)
            {
                T[] arr = _arrays[count];
                _arrays[count] = null;
                _count = count;

                return arr;
            }

            return null;
        }
    }
}
