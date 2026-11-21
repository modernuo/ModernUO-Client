// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers;

namespace Uo.Buffers;

/// <summary>
/// <see cref="BufferPool.Rent{T}"/> / <see cref="BufferPool.Return{T}"/> as an <see cref="ArrayPool{T}"/>, for the
/// single-thread ref collections (PooledRefList, PooledRefQueue, ValueStringBuilder). Routing them through the facade
/// keeps LOH-sized arrays out of the per-thread STArrayPool, which keeps up to 33 arrays per bucket for the thread's life.
/// </summary>
public sealed class LocalFamilyPool<T> : ArrayPool<T>
{
    public static readonly ArrayPool<T> Instance = new LocalFamilyPool<T>();

    private LocalFamilyPool()
    {
    }

    public override T[] Rent(int minimumLength) => BufferPool.Rent<T>(minimumLength);

    public override void Return(T[] array, bool clearArray = false) => BufferPool.Return(array, clearArray);
}
