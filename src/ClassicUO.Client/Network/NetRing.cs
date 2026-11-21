// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// IORingGroup code used under the BSD 3-Clause License; its full text (conditions and disclaimer) is in
// licenses/IORingGroup-LICENSE, which every build and publish copies next to the executable.
//
// Adapted from IORingGroup's IORingBuffer (IORingGroup/IORingBuffer.cs, commit 86efd04cdca8b202a42f33fddd0d95b962e36f7b):
// the double mapping on Windows (VirtualAlloc2 + MapViewOfFile3 placeholders, MapViewOfFileEx retry path before 1803),
// Linux (memfd + two MAP_FIXED views) and macOS (shm_open/unlink + two MAP_FIXED views), and the head/tail cursors. The
// client's version drops the send cursor (sends are synchronous), the pool tracking and FreeBSD, closes the Linux and
// macOS descriptor once both views exist (the mapping keeps the memory), and reports a failure instead of throwing so
// the caller can fall back to the CircularBuffer path.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassicUO.Network
{
    /// <summary>
    /// net_ring (net-rework step 3): a circular byte buffer whose physical pages are mapped twice, back to back, so the
    /// readable and the writable region are each one contiguous span even across the wrap. Not thread-safe: the receive
    /// ring is used by the main thread only, the send ring under NetClient's send lock.
    /// </summary>
    internal sealed unsafe partial class NetRing : IDisposable
    {
        private nint _handle;
        private nint _buffer;
        private readonly int _size;
        private readonly int _mask;
        private int _head; // read position, 0.._size-1
        private int _tail; // write position, 0.._size-1

        private NetRing(nint handle, nint buffer, int size)
        {
            _handle = handle;
            _buffer = buffer;
            _size = size;
            _mask = size - 1;
        }

        /// <summary>Physical size in bytes (the ring holds at most Size - 1).</summary>
        public int Size => _size;

        public int ReadableBytes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int head = _head, tail = _tail;

                return tail >= head ? tail - head : _size - head + tail;
            }
        }

        public int WritableBytes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _size - ReadableBytes - 1; // one byte kept free: full and empty differ
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetWriteSpan() => new((byte*)_buffer + _tail, WritableBytes);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetReadSpan() => new((byte*)_buffer + _head, ReadableBytes);

        public void CommitWrite(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, WritableBytes);
            _tail = (_tail + count) & _mask;
        }

        public void CommitRead(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, ReadableBytes);
            // no reset to 0 when empty: a step-4 posted receive may already target the tail
            _head = (_head + count) & _mask;
        }

        /// <summary>Drops everything unread (a connection change): the head jumps to the tail.</summary>
        public void DiscardReadable() => _head = _tail;

        /// <summary>Smallest size <see cref="TryCreate"/> accepts: the page size, or 64 KB on the Windows path before 1803.</summary>
        public static int MinimumSize =>
            OperatingSystem.IsWindows() && (ForceLegacyWindowsPath ?? !UsePlaceholderApi) ? WindowsAllocationGranularity : Environment.SystemPageSize;

        /// <summary>
        /// Maps a ring of <paramref name="size"/> bytes (a power of two and a multiple of <see cref="MinimumSize"/>). False with
        /// the reason when the size is wrong or the platform mapping failed (the caller falls back to the CircularBuffer path).
        /// </summary>
        public static bool TryCreate(int size, out NetRing ring, out string error)
        {
            ring = null;
            error = null;

            if (size <= 0 || size > int.MaxValue / 2 || (size & (size - 1)) != 0 || size % MinimumSize != 0)
            {
                error = $"size {size} is not a power of two multiple of {MinimumSize}";

                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    ring = (ForceLegacyWindowsPath ?? !UsePlaceholderApi) ? CreateWindowsLegacy(size, out error) : CreateWindowsPlaceholder(size, out error);
                }
                else if (OperatingSystem.IsLinux())
                {
                    ring = CreatePosix(size, true, out error);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    ring = CreatePosix(size, false, out error);
                }
                else
                {
                    error = "no double mapping on this platform";
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                ring = null;
                error = $"{e.GetType().Name}: {e.Message}";
            }

            return ring != null;
        }

        #region Windows

        private const int WindowsAllocationGranularity = 65536;

        // VirtualAlloc2 and MapViewOfFile3 exist from Windows 10 1803 on; resolved once.
        private static readonly bool UsePlaceholderApi = DetectPlaceholderApi();

        /// <summary>Tests: forces the legacy (pre-1803) or the placeholder path; null = detect.</summary>
        internal static bool? ForceLegacyWindowsPath;

        private static bool DetectPlaceholderApi()
        {
            if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("kernelbase.dll", out var lib))
            {
                return false;
            }

            try
            {
                return NativeLibrary.TryGetExport(lib, "VirtualAlloc2", out _) && NativeLibrary.TryGetExport(lib, "MapViewOfFile3", out _);
            }
            finally
            {
                NativeLibrary.Free(lib);
            }
        }

        private static NetRing CreateWindowsPlaceholder(int size, out string error)
        {
            error = null;
            var region = WindowsNative.VirtualAlloc2(0, 0, (ulong)size * 2, WindowsNative.MEM_RESERVE | WindowsNative.MEM_RESERVE_PLACEHOLDER, WindowsNative.PAGE_NOACCESS, 0, 0);

            if (region == 0)
            {
                error = $"VirtualAlloc2 failed: {Marshal.GetLastPInvokeError()}";

                return null;
            }

            // split the placeholder in two
            if (!WindowsNative.VirtualFree(region, (uint)size, WindowsNative.MEM_RELEASE | WindowsNative.MEM_PRESERVE_PLACEHOLDER))
            {
                error = $"VirtualFree (split) failed: {Marshal.GetLastPInvokeError()}";
                WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);

                return null;
            }

            var handle = WindowsNative.CreateFileMappingW(WindowsNative.InvalidHandleValue, 0, WindowsNative.PAGE_READWRITE, 0, (uint)size, null);

            if (handle == 0)
            {
                error = $"CreateFileMapping failed: {Marshal.GetLastPInvokeError()}";
                WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);
                WindowsNative.VirtualFree(region + size, 0, WindowsNative.MEM_RELEASE);

                return null;
            }

            var view1 = WindowsNative.MapViewOfFile3(handle, 0, region, 0, (ulong)size, WindowsNative.MEM_REPLACE_PLACEHOLDER, WindowsNative.PAGE_READWRITE, 0, 0);

            if (view1 == 0)
            {
                error = $"MapViewOfFile3 (first) failed: {Marshal.GetLastPInvokeError()}";
                WindowsNative.CloseHandle(handle);
                WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);
                WindowsNative.VirtualFree(region + size, 0, WindowsNative.MEM_RELEASE);

                return null;
            }

            var view2 = WindowsNative.MapViewOfFile3(handle, 0, view1 + size, 0, (ulong)size, WindowsNative.MEM_REPLACE_PLACEHOLDER, WindowsNative.PAGE_READWRITE, 0, 0);

            if (view2 == 0)
            {
                error = $"MapViewOfFile3 (second) failed: {Marshal.GetLastPInvokeError()}";
                WindowsNative.UnmapViewOfFile(view1);
                WindowsNative.VirtualFree(region + size, 0, WindowsNative.MEM_RELEASE);
                WindowsNative.CloseHandle(handle);

                return null;
            }

            return new NetRing(handle, view1, size);
        }

        // Before 1803: reserve a 2x hole, release it and map both views into it; another allocation can take the hole in
        // between, so retry a bounded number of times.
        private static NetRing CreateWindowsLegacy(int size, out string error)
        {
            error = null;
            var handle = WindowsNative.CreateFileMappingW(WindowsNative.InvalidHandleValue, 0, WindowsNative.PAGE_READWRITE, 0, (uint)size, null);

            if (handle == 0)
            {
                error = $"CreateFileMapping failed: {Marshal.GetLastPInvokeError()}";

                return null;
            }

            for (int attempt = 0; attempt < 100; attempt++)
            {
                var region = WindowsNative.VirtualAlloc(0, (nuint)((long)size * 2), WindowsNative.MEM_RESERVE, WindowsNative.PAGE_NOACCESS);

                if (region == 0)
                {
                    error = $"VirtualAlloc (reserve) failed: {Marshal.GetLastPInvokeError()}";
                    WindowsNative.CloseHandle(handle);

                    return null;
                }

                WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);
                var view1 = WindowsNative.MapViewOfFileEx(handle, WindowsNative.FILE_MAP_READ | WindowsNative.FILE_MAP_WRITE, 0, 0, (nuint)size, region);

                if (view1 == 0)
                {
                    continue;
                }

                var view2 = WindowsNative.MapViewOfFileEx(handle, WindowsNative.FILE_MAP_READ | WindowsNative.FILE_MAP_WRITE, 0, 0, (nuint)size, region + size);

                if (view2 == 0)
                {
                    WindowsNative.UnmapViewOfFile(view1);

                    continue;
                }

                return new NetRing(handle, view1, size);
            }

            error = "no contiguous double-mapped region after 100 attempts";
            WindowsNative.CloseHandle(handle);

            return null;
        }

        private static partial class WindowsNative
        {
            public const nint InvalidHandleValue = -1;
            public const uint MEM_PRESERVE_PLACEHOLDER = 0x02;
            public const uint MEM_RESERVE = 0x2000;
            public const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
            public const uint MEM_RELEASE = 0x8000;
            public const uint MEM_RESERVE_PLACEHOLDER = 0x40000;
            public const uint PAGE_NOACCESS = 0x01;
            public const uint PAGE_READWRITE = 0x04;
            public const uint FILE_MAP_WRITE = 0x0002;
            public const uint FILE_MAP_READ = 0x0004;

            [LibraryImport("kernelbase.dll", SetLastError = true)]
            public static partial nint VirtualAlloc2(nint process, nint address, ulong size, uint allocationType, uint protect, nint extendedParameters, uint parameterCount);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool VirtualFree(nint lpAddress, uint dwSize, uint dwFreeType);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            public static partial nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            public static partial nint MapViewOfFileEx(nint hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap, nint lpBaseAddress);

            [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
            public static partial nint CreateFileMappingW(nint hFile, nint lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

            [LibraryImport("kernelbase.dll", SetLastError = true)]
            public static partial nint MapViewOfFile3(nint hFileMappingObject, nint processHandle, nint pvBaseAddress, ulong ullOffset, ulong ullSize, uint allocFlags, uint dwDesiredAccess, nint hExtendedParameter, int parameterCount);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool UnmapViewOfFile(nint lpBaseAddress);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool CloseHandle(nint hObject);
        }

        #endregion

        #region Linux and macOS

        private static int s_shmCounter;

        // Linux: memfd_create; macOS: shm_open + shm_unlink (unnamed once both ends hold it). Then reserve 2x with PROT_NONE
        // and map the descriptor twice over it with MAP_FIXED. The descriptor is closed afterwards: the views keep the pages.
        private static NetRing CreatePosix(int size, bool linux, out string error)
        {
            error = null;
            int fd;

            if (linux)
            {
                fd = PosixNative.memfd_create("cuo_net_ring", PosixNative.MFD_CLOEXEC);
            }
            else
            {
                // macOS limits shm names to about 30 chars
                string name = $"/cuo{Environment.ProcessId:X}_{Interlocked.Increment(ref s_shmCounter):X}";
                fd = PosixNative.shm_open(name, PosixNative.MacO_RDWR | PosixNative.MacO_CREAT | PosixNative.MacO_EXCL, 0x180 /* 0600 */);

                if (fd >= 0)
                {
                    PosixNative.shm_unlink(name);
                }
            }

            if (fd < 0)
            {
                error = $"{(linux ? "memfd_create" : "shm_open")} failed: {Marshal.GetLastPInvokeError()}";

                return null;
            }

            try
            {
                if (PosixNative.ftruncate(fd, size) < 0)
                {
                    error = $"ftruncate failed: {Marshal.GetLastPInvokeError()}";

                    return null;
                }

                int anon = linux ? PosixNative.LinuxMAP_ANONYMOUS : PosixNative.MacMAP_ANON;
                var region = PosixNative.mmap(0, (nuint)((long)size * 2), PosixNative.PROT_NONE, PosixNative.MAP_PRIVATE | anon, -1, 0);

                if (region == PosixNative.MAP_FAILED)
                {
                    error = $"mmap (reserve) failed: {Marshal.GetLastPInvokeError()}";

                    return null;
                }

                var view1 = PosixNative.mmap(region, (nuint)size, PosixNative.PROT_READ | PosixNative.PROT_WRITE, PosixNative.MAP_SHARED | PosixNative.MAP_FIXED, fd, 0);
                var view2 = view1 == PosixNative.MAP_FAILED
                    ? PosixNative.MAP_FAILED
                    : PosixNative.mmap(region + size, (nuint)size, PosixNative.PROT_READ | PosixNative.PROT_WRITE, PosixNative.MAP_SHARED | PosixNative.MAP_FIXED, fd, 0);

                if (view2 == PosixNative.MAP_FAILED)
                {
                    error = $"mmap (views) failed: {Marshal.GetLastPInvokeError()}";
                    PosixNative.munmap(region, (nuint)((long)size * 2));

                    return null;
                }

                return new NetRing(0, region, size);
            }
            finally
            {
                PosixNative.close(fd);
            }
        }

        private static partial class PosixNative
        {
            public static readonly nint MAP_FAILED = -1;
            public const uint MFD_CLOEXEC = 0x0001;
            public const int PROT_NONE = 0x0;
            public const int PROT_READ = 0x1;
            public const int PROT_WRITE = 0x2;
            public const int MAP_SHARED = 0x01;
            public const int MAP_PRIVATE = 0x02;
            public const int MAP_FIXED = 0x10;
            public const int LinuxMAP_ANONYMOUS = 0x20;
            public const int MacMAP_ANON = 0x1000;
            public const int MacO_RDWR = 0x0002;
            public const int MacO_CREAT = 0x0200;
            public const int MacO_EXCL = 0x0800;

            [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
            public static partial int memfd_create(string name, uint flags);

            [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
            public static partial int shm_open(string name, int oflag, int mode);

            [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
            public static partial int shm_unlink(string name);

            [LibraryImport("libc", SetLastError = true)]
            public static partial int ftruncate(int fd, long length);

            [LibraryImport("libc", SetLastError = true)]
            public static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

            [LibraryImport("libc", SetLastError = true)]
            public static partial int munmap(nint addr, nuint length);

            [LibraryImport("libc", SetLastError = true)]
            public static partial int close(int fd);
        }

        #endregion

        private void Release()
        {
            if (_buffer == 0)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                WindowsNative.UnmapViewOfFile(_buffer);
                WindowsNative.UnmapViewOfFile(_buffer + _size);
                WindowsNative.CloseHandle(_handle);
                _handle = 0;
            }
            else
            {
                PosixNative.munmap(_buffer, (nuint)((long)_size * 2));
            }

            _buffer = 0;
        }

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        ~NetRing() => Release();
    }
}
