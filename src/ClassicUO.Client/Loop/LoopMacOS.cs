// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// IORingGroup code used under the BSD 3-Clause License; its full text (conditions and disclaimer) is in
// licenses/IORingGroup-LICENSE, which every build and publish copies next to the executable.
//
// The kqueue/kevent/close declarations, the kevent and timespec structs (kevent_t, timespec_t here) and the EVFILT/EV/NOTE constants below are copied
// from IORingGroup (IORingGroup/Darwin/DarwinIORingGroup.cs, commit 86efd04cdca8b202a42f33fddd0d95b962e36f7b), BSD reuse
// approved by the owner (2026-11-19). The CoreFoundation run-loop declarations are added here for the client's event loop.

using System.Runtime.InteropServices;

namespace ClassicUO.Loop
{
    internal static unsafe class LoopMacOS
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";
        public const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public const short EVFILT_READ = -1;
        public const short EVFILT_USER = -10;

        public const ushort EV_ADD = 0x0001;
        public const ushort EV_DELETE = 0x0002;
        public const ushort EV_CLEAR = 0x0020;
        public const ushort EV_ERROR = 0x4000;

        public const uint NOTE_TRIGGER = 0x01000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct kevent_t
        {
            public nint ident;
            public short filter;
            public ushort flags;
            public uint fflags;
            public nint data;
            public nint udata;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct timespec_t
        {
            public long tv_sec;
            public long tv_nsec;
        }

        [DllImport(LibSystem, SetLastError = true)]
        public static extern int kqueue();

        [DllImport(LibSystem, SetLastError = true)]
        public static extern int kevent(int kq, kevent_t* changelist, int nchanges, kevent_t* eventlist, int nevents, timespec_t* timeout);

        [DllImport(LibSystem, SetLastError = true)]
        public static extern int close(int fd);

        // client additions: a CFFileDescriptor on the kqueue fd is a source of the main thread's run loop, so its callback runs
        // inside the Cocoa wait (nextEventMatchingMask:untilDate:) that SDL_WaitEventTimeoutNS runs
        public const nuint kCFFileDescriptorReadCallBack = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct CFFileDescriptorContext
        {
            public nint version;
            public nint info;
            public nint retain;
            public nint release;
            public nint copyDescription;
        }

        [DllImport(CoreFoundationPath)]
        public static extern nint CFFileDescriptorCreate(nint allocator, int fd, byte closeOnInvalidate,
            delegate* unmanaged<nint, nuint, nint, void> callout, CFFileDescriptorContext* context);

        [DllImport(CoreFoundationPath)]
        public static extern void CFFileDescriptorEnableCallBacks(nint f, nuint callBackTypes);

        [DllImport(CoreFoundationPath)]
        public static extern void CFFileDescriptorInvalidate(nint f);

        [DllImport(CoreFoundationPath)]
        public static extern nint CFFileDescriptorCreateRunLoopSource(nint allocator, nint f, nint order);

        [DllImport(CoreFoundationPath)]
        public static extern nint CFRunLoopGetMain();

        [DllImport(CoreFoundationPath)]
        public static extern void CFRunLoopAddSource(nint rl, nint source, nint mode);

        [DllImport(CoreFoundationPath)]
        public static extern void CFRunLoopRemoveSource(nint rl, nint source, nint mode);

        [DllImport(CoreFoundationPath)]
        public static extern void CFRelease(nint cf);

        /// <summary>kCFRunLoopCommonModes (a CFStringRef global), or 0 if it cannot be read.</summary>
        public static nint CommonModes()
        {
            if (!NativeLibrary.TryLoad(CoreFoundationPath, out nint lib) || !NativeLibrary.TryGetExport(lib, "kCFRunLoopCommonModes", out nint p))
            {
                return 0;
            }

            return *(nint*)p;
        }
    }
}
