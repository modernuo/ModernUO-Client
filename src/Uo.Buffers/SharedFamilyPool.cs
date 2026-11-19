// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers;

namespace Uo.Buffers;

/// <summary>
/// <see cref="BufferPool.RentShared{T}"/> / <see cref="BufferPool.ReturnShared{T}"/> as an <see cref="ArrayPool{T}"/>,
/// for code that takes a pool instance (animation frame buffers: decoded on a worker, returned on the main thread).
/// </summary>
public sealed class SharedFamilyPool<T> : ArrayPool<T>
{
    public static readonly SharedFamilyPool<T> Instance = new SharedFamilyPool<T>();

    private SharedFamilyPool()
    {
    }

    public override T[] Rent(int minimumLength) => BufferPool.RentShared<T>(minimumLength);

    public override void Return(T[] array, bool clearArray = false) => BufferPool.ReturnShared(array, clearArray);
}
