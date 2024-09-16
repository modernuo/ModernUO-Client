// SPDX-License-Identifier: BSD-2-Clause
// author: Max Kellermann <max.kellermann@gmail.com>

using System;
using System.Runtime.CompilerServices;

namespace ClassicUO.IO;

/// <summary>
///     A buffer with a fixed address, i.e. the allocation is pinned.
/// </summary>
public unsafe class PinnedBuffer : IDisposable
{
    private byte* _data;

    public bool HasData => _data != null;

    public long Length { get; private set; }

    public byte *Data => _data;
    public IntPtr StartAddress => (IntPtr) _data;
    public IntPtr EndAddress => StartAddress + (IntPtr)Length;

    ~PinnedBuffer()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void SetData(byte* data, long length)
    {
        _data = data;
        Length = length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> AsSpan()
    {
        return new ReadOnlySpan<byte>(Data, (int)Length);
    }
}
