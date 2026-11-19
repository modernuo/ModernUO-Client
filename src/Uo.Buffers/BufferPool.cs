// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Uo.Buffers;

/// <summary>
/// The one entry point for pooled arrays. Three classes, chosen by size and by whether the array leaves its thread:
/// <list type="bullet">
/// <item>Small (pooled length at most <see cref="SmallMaxBytes"/>), same thread: <see cref="Rent{T}"/> /
/// <see cref="Return{T}"/> use the calling thread's <see cref="STArrayPool{T}"/>. Rent and return on one thread
/// (scoped scratch, ref structs).</item>
/// <item>Small, crosses threads (worker decodes, main returns): <see cref="RentShared{T}"/> /
/// <see cref="ReturnShared{T}"/> use <see cref="ArrayPool{T}.Shared"/>.</item>
/// <item>Large (above <see cref="SmallMaxBytes"/>), any thread: both families route to <see cref="LargeBufferPool"/>
/// (bounded free bytes, size classes of 25 %).</item>
/// </list>
/// Return routes by the array's length with the same rule as Rent, so a caller does not track where an array came
/// from; it only keeps the family (Rent with Return, RentShared with ReturnShared).
/// </summary>
public static class BufferPool
{
    /// <summary>Largest small array in bytes: power-of-two pooled lengths up to here stay below the LOH (85,000 B).</summary>
    public const int SmallMaxBytes = 64 << 10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSmall<T>(long length) => length * Unsafe.SizeOf<T>() <= SmallMaxBytes;

    /// <summary>Same-thread rent: return it on this thread with <see cref="Return{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] Rent<T>(int minimumLength) =>
        IsSmall<T>(minimumLength) ? STArrayPool<T>.ThreadLocal.Rent(minimumLength) : LargeBufferPool.Rent<T>(minimumLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return<T>(T[] array, bool clearArray = false)
    {
        if (array is null)
        {
            return;
        }

        if (IsSmall<T>(array.Length))
        {
            STArrayPool<T>.ThreadLocal.Return(array, clearArray);
        }
        else
        {
            if (clearArray)
            {
                Array.Clear(array);
            }

            LargeBufferPool.Return(array);
        }
    }

    /// <summary>Rent for an array that another thread returns (with <see cref="ReturnShared{T}"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] RentShared<T>(int minimumLength) =>
        IsSmall<T>(minimumLength) ? ArrayPool<T>.Shared.Rent(minimumLength) : LargeBufferPool.Rent<T>(minimumLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnShared<T>(T[] array, bool clearArray = false)
    {
        if (array is null)
        {
            return;
        }

        if (IsSmall<T>(array.Length))
        {
            ArrayPool<T>.Shared.Return(array, clearArray);
        }
        else
        {
            if (clearArray)
            {
                Array.Clear(array);
            }

            LargeBufferPool.Return(array);
        }
    }

    /// <summary>The length a rent of <paramref name="minimumLength"/> returns (either family).</summary>
    public static int PooledLength<T>(int minimumLength)
    {
        if (minimumLength <= 0)
        {
            return 0;
        }

        if (IsSmall<T>(minimumLength))
        {
            return minimumLength <= 16 ? 16 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)minimumLength);
        }

        return LargeBufferPool.PooledLength<T>(minimumLength);
    }

    /// <summary>The bytes a rent of <paramref name="minimumLength"/> holds (what a decode budget counts).</summary>
    public static long PooledBytes<T>(int minimumLength) => (long)PooledLength<T>(minimumLength) * Unsafe.SizeOf<T>();
}
